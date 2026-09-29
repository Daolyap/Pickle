using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pickle.Abstractions;

namespace Pickle.Core.Commands;

/// <summary>
/// <c>pk bugreport</c>: one zip to attach to an issue — versions, OS and terminal, <c>pk doctor</c>, plugins, the config
/// files and the end of the newest logs. Secrets (keys named like tokens or passwords, credentials in URLs) are masked
/// and the home folder and user name are replaced; history, aliases and the profile are never included.
/// </summary>
public sealed partial class BugReportCommand(PickleRuntime runtime) : IPickleCommand
{
    private const int LogTailBytes = 256 * 1024;
    private const int LogFiles = 3;

    private static readonly string[] SecretKeyParts = ["token", "password", "passwd", "secret", "apikey", "api_key", "credential", "pat", "privatekey", "connectionstring"];

    public string Name => "bugreport";

    public string Description => "Collect versions, pk doctor, config and recent logs (secrets removed) into a zip for an issue";

    public string Usage => "pk bugreport [--out <file.zip>] [--no-logs]";

    public async ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var list = args.ToList();
        var noLogs = list.RemoveAll(a => a.Equals("--no-logs", StringComparison.OrdinalIgnoreCase)) > 0;
        var outIndex = list.FindIndex(a => a.Equals("--out", StringComparison.OrdinalIgnoreCase));
        string? output = null;
        if (outIndex >= 0)
        {
            if (outIndex + 1 >= list.Count)
            {
                context.WriteError("Usage: " + Usage);
                return 2;
            }

            output = list[outIndex + 1];
            list.RemoveRange(outIndex, 2);
        }

        if (list.Count > 0)
        {
            context.WriteError($"Unknown argument '{list[0]}'. Usage: {Usage}");
            return 2;
        }

        var file = Path.GetFullPath(output ?? $"pickle-bugreport-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip", context.Cwd);
        var checks = await new DoctorCommand(runtime).RunChecksAsync(cancellationToken).ConfigureAwait(false);
        var scrub = Scrubber(SegmentsHome(), Environment.UserName);

        await using (var stream = File.Create(file))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Add(zip, "report.md", scrub(Report(checks)));
            foreach (var (name, path) in new[] { ("config.json", runtime.Paths.ConfigFile), ("config.local.json", runtime.Paths.LocalConfigFile) })
            {
                if (ReadText(path) is { } text)
                {
                    Add(zip, name, scrub(RedactJson(text)));
                }
            }

            if (!noLogs && Directory.Exists(runtime.Paths.LogDir))
            {
                foreach (var log in new DirectoryInfo(runtime.Paths.LogDir).EnumerateFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(LogFiles))
                {
                    if (Tail(log.FullName, LogTailBytes) is { } text)
                    {
                        Add(zip, "logs/" + log.Name, scrub(RedactText(text)));
                    }
                }
            }
        }

        var theme = runtime.Themes.Current;
        context.WriteHost("Bug report written to " + Ansi.Colorize(file, theme.Ui.Accent, bold: true));
        context.WriteHost(Ansi.Colorize(
            "Secrets and your user name are removed, but please look through it before attaching it to an issue: https://github.com/Daolyap/Pickle/issues/new",
            theme.Ui.Muted));
        return 0;
    }

    /// <summary>Masks the values of JSON properties whose names look secret, and credentials in URL values.</summary>
    public static string RedactJson(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return RedactText(json);
        }

        Walk(root);
        return root?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? string.Empty;

        static void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj.ToList())
                    {
                        if (value is JsonValue && LooksSecret(key))
                        {
                            obj[key] = "***";
                        }
                        else if (value is JsonValue v && v.TryGetValue<string>(out var text))
                        {
                            obj[key] = RedactText(text);
                        }
                        else
                        {
                            Walk(value);
                        }
                    }

                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (array[i] is JsonValue v && v.TryGetValue<string>(out var text))
                        {
                            array[i] = RedactText(text);
                        }
                        else
                        {
                            Walk(array[i]);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>Removes URL credentials (https://user:pass@host), bearer tokens and key=value secrets from free text.</summary>
    public static string RedactText(string text)
    {
        text = UrlCredentials().Replace(text, "${scheme}***@");
        text = Bearer().Replace(text, "${prefix}***");
        return KeyValueSecret().Replace(text, "${key}${sep}***");
    }

    /// <summary>Replaces the home folder with ~ and the user name with &lt;user&gt; (both case-insensitively).</summary>
    public static Func<string, string> Scrubber(string? home, string? user) => text =>
    {
        if (!string.IsNullOrEmpty(home) && home.Length > 3)
        {
            text = text.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(home.Replace('\\', '/'), "~", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrEmpty(user) && user.Length >= 3)
        {
            text = Regex.Replace(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(user) + @"(?![\p{L}\p{N}])", "<user>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }

        return text;
    };

    private static bool LooksSecret(string key)
    {
        var normalized = key.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return SecretKeyParts.Any(part => part == "pat" ? normalized is "pat" or "githubpat" : normalized.Contains(part.Replace("_", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal));
    }

    private static string? SegmentsHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : home;
    }

    private string Report(IReadOnlyList<DoctorCheck> checks)
    {
        var sb = new StringBuilder();
        var terminal = runtime.Terminal;
        sb.AppendLine("# Pickle bug report").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Created: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Pickle {PickleRuntime.Version} · PowerShell {PickleRuntime.PowerShellVersion} · .NET {Environment.Version}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), process {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Elevated: {(runtime.IsElevated ? "yes" : "no")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Terminal: {terminal.Width}×{terminal.Height}, ANSI {(terminal.SupportsAnsi ? "yes" : "no")}, interactive {(terminal.IsInteractive ? "yes" : "no")}, "
            + $"TERM={Environment.GetEnvironmentVariable("TERM") ?? "-"}, Windows Terminal {(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")) ? "no" : "yes")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Theme: {runtime.Themes.Current.Name} (configured: {runtime.Config.Current.Theme})");
        sb.AppendLine().AppendLine("## pk doctor").AppendLine().AppendLine("| Status | Check | Detail |").AppendLine("|---|---|---|");
        foreach (var check in checks)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {check.Status} | {Cell(check.Check)} | {Cell(check.Detail)} |");
        }

        sb.AppendLine().AppendLine("## Plugins").AppendLine();
        foreach (var plugin in runtime.Plugins.Loaded)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {plugin.Id} ({plugin.Kind}){(plugin.Enabled ? string.Empty : ", disabled")}{(plugin.Error is { } e ? ", error: " + RedactText(e) : string.Empty)}");
        }

        return sb.ToString();

        static string Cell(string text) => RedactText(text).Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ');
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The last bytes of a log that may still be open for writing, starting at a line boundary.
    private static string? Tail(string path, int bytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - bytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            return start > 0 && text.IndexOf('\n', StringComparison.Ordinal) is var newline and >= 0 ? text[(newline + 1)..] : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(?<scheme>[a-zA-Z][a-zA-Z0-9+.-]*://)[^/\s:@]+(:[^/\s@]*)?@")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(?<prefix>\b(?:Bearer|token)\s+)[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?<key>\b(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key)\b)(?<sep>\s*[=:]\s*)[^\s;,&""']+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecret();
}
