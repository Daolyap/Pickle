using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Winget;

/// <summary>
/// The winget source repair: one fixed Windows PowerShell script (no caller input) that re-registers
/// <c>source.msix</c> for the account it runs as and prints enough detail to diagnose a failure (the error record, its
/// HRESULT and the AppX deployment log). Used unelevated by <see cref="WingetService"/> and elevated by the helper.
/// </summary>
internal static partial class WingetSourceRepair
{
    public const string Marker = "PICKLE-REPAIR:";

    // -ForceApplicationShutdown: winget's COM server (kept alive by Microsoft.WinGet.Client, e.g. the winget panel)
    // holds the source index open, which fails the update with 0x80073D02 (resources in use).
    // $ProgressPreference: the deployment progress bar is noise in captured output.
    public const string Script = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $url = 'https://cdn.winget.microsoft.com/cache/source.msix'
        "Add-AppxPackage -Path $url -ForceApplicationShutdown"
        "Account: $([Environment]::UserDomainName)\$([Environment]::UserName)"
        try {
            # By path: elevated, a module named Appx in a user-writable PSModulePath folder must not be loaded instead.
            Import-Module (Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\Modules\Appx\Appx.psd1') -ErrorAction Stop
            Add-AppxPackage -Path $url -ForceApplicationShutdown -ErrorAction Stop
            'PICKLE-REPAIR: OK'
            exit 0
        } catch {
            $record = $_
            ($record | Out-String -Width 160).TrimEnd()
            $text = [string]$record.Exception.Message
            $activity = [regex]::Match($text, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')
            if ($activity.Success -and (Get-Command Get-AppxLog -ErrorAction SilentlyContinue)) {
                '--- Get-AppxLog -ActivityId ' + $activity.Value + ' ---'
                try {
                    Get-AppxLog -ActivityId $activity.Value -ErrorAction Stop |
                        Select-Object -Last 15 |
                        ForEach-Object { '{0:HH:mm:ss} {1}' -f $_.TimeCreated, ([string]$_.Message -replace '\s+', ' ') }
                } catch { 'Get-AppxLog failed: ' + $_.Exception.Message }
            }
            'PICKLE-REPAIR: FAIL 0x{0:X8}' -f $record.Exception.HResult
            exit 1
        }
        """;

    private static readonly Dictionary<uint, string> Known = new()
    {
        [0x80073D02] = "The source package is in use. Close winget (and other Pickle or terminal windows using it) and try again.",
        [0x80073CF0] = "The downloaded source.msix could not be opened (download interrupted or blocked by a proxy).",
        [0x80073CF3] = "The source package failed dependency or conflict validation.",
        [0x80073CF6] = "The source package could not be registered.",
        [0x80073CF9] = "The package deployment failed; see the AppX log below.",
        [0x80073CFB] = "The same version is already installed with different contents. Run 'winget source reset --force' in an elevated shell.",
        [0x80073D05] = "Removing the old source package data failed. Sign out and in again, then retry.",
        [0x80073D0A] = "The package could not be installed because the Windows Firewall service is not running.",
        [0x80073D19] = "The source package deployment failed because the user profile is not loaded (another account?).",
        [0x80070005] = "Access denied.",
        [0x80072EE7] = "cdn.winget.microsoft.com could not be resolved. Check your internet connection or proxy.",
        [0x80072EFD] = "cdn.winget.microsoft.com could not be reached. Check your internet connection or proxy.",
        [0x80072F8F] = "The secure connection to cdn.winget.microsoft.com failed (check the system clock and TLS settings).",
        [0x80190194] = "source.msix was not found on the CDN (HTTP 404).",
    };

    private const uint HigherVersionInstalled = 0x80073D06;

    /// <summary><c>powershell.exe</c> arguments: the script travels as -EncodedCommand (no command-line quoting).</summary>
    public static IReadOnlyList<string> Arguments() =>
        ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script))];

    /// <summary>
    /// Environment for the Windows PowerShell child: Pickle prepends PowerShell 7's module folders to PSModulePath, which
    /// makes Windows PowerShell 5.1 load 7.x modules it can't run. Use the machine value instead (null removes it).
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Environment() =>
        new Dictionary<string, string?>
        {
            ["PSModulePath"] = System.Environment.GetEnvironmentVariable("PSModulePath", EnvironmentVariableTarget.Machine) is { Length: > 0 } machine ? machine : null,
        };

    public static WingetOperationResult Interpret(int exitCode, string output, bool timedOut, string who)
    {
        var text = Clean(output);
        if (timedOut)
        {
            return new WingetOperationResult(false, "Repairing the winget source timed out.", -1) { Output = text };
        }

        var marker = MarkerRegex().Matches(text).LastOrDefault();
        if (exitCode == 0 && (marker is null || marker.Groups["ok"].Success))
        {
            return new WingetOperationResult(true, $"The winget source package was re-registered {who}.", 0) { Output = text };
        }

        // The deployment HRESULT is in the message ("Deployment failed with HRESULT: 0x80073D02"); the exception's own
        // HResult is often a generic .NET code.
        var hresult = HResultRegex().Match(text) is { Success: true } h ? ParseHex(h.Groups[1].Value) : null;
        hresult ??= marker is { Success: true } m && m.Groups["hr"].Success ? ParseHex(m.Groups["hr"].Value) : null;
        if (hresult == HigherVersionInstalled)
        {
            return new WingetOperationResult(
                true,
                "A newer winget source package is already installed; nothing to re-register. If winget still fails, run 'winget source reset --force' in an elevated shell.",
                0)
            { Output = text };
        }

        var code = hresult is { } hr ? unchecked((int)hr) : exitCode;
        var reason = hresult is { } known && Known.TryGetValue(known, out var message)
            ? message
            : FirstErrorLine(text) ?? "Add-AppxPackage failed.";
        var hex = hresult is { } value ? " (0x" + value.ToString("X8", CultureInfo.InvariantCulture) + ")" : exitCode != 0 ? $" (exit code {exitCode})" : string.Empty;
        return new WingetOperationResult(false, $"Repairing the winget source failed: {reason}{hex}", code) { Output = text };
    }

    /// <summary>Output without the marker lines, CR/LF normalized, blank runs collapsed.</summary>
    public static string Clean(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
            .Where(l => !l.StartsWith(Marker, StringComparison.Ordinal) && !l.StartsWith("#< CLIXML", StringComparison.Ordinal) && !l.StartsWith("<Objs ", StringComparison.Ordinal))
            .Select(l => l.TrimEnd())
            .ToList();
        var sb = new StringBuilder();
        var blank = false;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                blank = sb.Length > 0;
                continue;
            }

            if (blank)
            {
                sb.Append('\n');
                blank = false;
            }

            sb.Append(line).Append('\n');
        }

        // Keep the marker's verdict visible in the text (Interpret reads it before Clean drops it).
        var verdict = MarkerRegex().Matches(output).LastOrDefault();
        if (verdict is { Groups: var g } && g["hr"].Success)
        {
            sb.Append("HRESULT ").Append(g["hr"].Value).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    private static string? FirstErrorLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Add-AppxPackage", StringComparison.OrdinalIgnoreCase) && line.Contains(':', StringComparison.Ordinal))
            {
                return line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
            }

            if (line.StartsWith("Deployment failed", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }

        return null;
    }

    private static uint? ParseHex(string value) =>
        uint.TryParse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    [GeneratedRegex(@"^PICKLE-REPAIR: (?:(?<ok>OK)|FAIL (?<hr>0x[0-9A-Fa-f]{8}))\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex MarkerRegex();

    [GeneratedRegex(@"HRESULT:?\s*(0x[0-9A-Fa-f]{8})", RegexOptions.CultureInvariant)]
    private static partial Regex HResultRegex();
}
