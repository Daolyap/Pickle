using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Windows.Processes;
using Pickle.Windows.TaskScheduler;
using Pickle.Windows.WindowsUpdate;
using Pickle.Windows.Winget;

namespace Pickle.Windows.Elevation;

/// <summary>
/// The real elevated operations. Only fixed programs from admin-only locations are started (System32 PowerShell,
/// winget.exe from the App Installer package folder), with arguments from <see cref="ElevatedOperations"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsElevatedExecutor(IPickleLogger log, IProcessRunner? runner = null) : IElevatedExecutor
{
    private readonly IProcessRunner _runner = runner ?? new ProcessRunner(log);

    public async Task<ElevatedResponse> RepairWingetSourceAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        progress.Report($"Re-registering the winget source package for {Environment.UserDomainName}\\{Environment.UserName} (elevated)…");
        var result = await _runner.RunAsync(
            WingetService.WindowsPowerShellPath,
            WingetSourceRepair.Arguments(),
            null,
            ElevatedOperations.TimeoutFor(ElevatedOperationKind.WingetRepairSource),
            cancellationToken,
            WingetSourceRepair.Environment()).ConfigureAwait(false);
        var outcome = WingetSourceRepair.Interpret(result.ExitCode, result.Output, result.TimedOut, "for the elevated account");
        return new ElevatedResponse(outcome.Success, outcome.Message, outcome.ExitCode ?? 0, outcome.Output);
    }

    public async Task<ElevatedResponse> RunWingetAsync(IReadOnlyList<string> arguments, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var exe = WingetLocator.FindTrusted(log);
        if (exe is null)
        {
            return new ElevatedResponse(false, "winget.exe was not found in the App Installer package folder.", 2);
        }

        var tracker = new WingetProgressTracker(new SyncProgress<WingetProgress>(p =>
            progress.Report(p.Percent is { } percent ? $"{p.Stage} {percent:0}%" : p.Message ?? p.Stage)));
        var result = await _runner.RunAsync(exe, arguments, tracker.Feed, ElevatedOperations.TimeoutFor(ElevatedOperationKind.WingetUpgrade), cancellationToken).ConfigureAwait(false);
        var idIndex = arguments.ToList().IndexOf("--id");
        var target = idIndex >= 0 && idIndex + 1 < arguments.Count ? arguments[idIndex + 1] : "all packages";
        var outcome = WingetErrors.FromExitCode(result.TimedOut ? -1 : result.ExitCode, result.Output, arguments[0], target);
        return new ElevatedResponse(outcome.Success, outcome.Message, outcome.ExitCode ?? 0, WingetCliParser.Transcript(result.Output));
    }

    public async Task<ElevatedResponse> InstallWindowsUpdatesAsync(IReadOnlyList<string> updateIds, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var summary = await WuaClient.RunAsync(
            () => WuaClient.Install(updateIds, p => progress.Report(Describe(p)), cancellationToken),
            ElevatedOperations.TimeoutFor(ElevatedOperationKind.WindowsUpdateInstall),
            cancellationToken).ConfigureAwait(false);
        var result = WindowsUpdateService.ToResult(summary);
        return new ElevatedResponse(result.Success, result.Message, 0, JsonSerializer.Serialize(Trim(summary), PickleJson.Compact));
    }

    public Task<ElevatedResponse> RegisterTaskAsync(ScheduledTaskDefinition definition, IProgress<string> progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            progress.Report($"Registering {definition.Folder}\\{definition.Name} with highest privileges…");
            try
            {
                var info = TaskRegistrar.Register(definition with { RunElevated = true });
                return new ElevatedResponse(true, $"Registered {info.Path} (runs with highest privileges).");
            }
            catch (COMException ex)
            {
                return new ElevatedResponse(false, $"Registering the task failed: {ex.Message}", ex.HResult);
            }
        }, cancellationToken);

    private static string Describe(WindowsUpdateProgress p) =>
        p.Percent is { } percent ? $"{p.Stage} {p.CurrentUpdate} {percent:0}%".Replace("  ", " ", StringComparison.Ordinal) : $"{p.Stage} {p.CurrentUpdate}".TrimEnd();

    private static WuaInstallSummary Trim(WuaInstallSummary summary) =>
        summary with { Items = [.. summary.Items.Take(100).Select(i => i with { Title = Cut(i.Title, 200), Error = i.Error is null ? null : Cut(i.Error, 300) })] };

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>Finds winget.exe inside the App Installer package folder (admin-only writable), never the per-user alias.</summary>
[SupportedOSPlatform("windows")]
internal static class WingetLocator
{
    private const string PackagePrefix = "Microsoft.DesktopAppInstaller_";
    private const string PublisherSuffix = "__8wekyb3d8bbwe";

    public static string? FindTrusted(IPickleLogger log)
    {
        try
        {
            var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                _ => "x64",
            };

            var best = Directory.EnumerateDirectories(windowsApps, PackagePrefix + "*" + PublisherSuffix)
                .Select(dir => (Dir: dir, Name: Path.GetFileName(dir)))
                .Select(d => (d.Dir, Parts: d.Name[PackagePrefix.Length..^PublisherSuffix.Length].Split('_')))
                .Where(d => d.Parts.Length >= 2 && (d.Parts[1] == arch || d.Parts[1] == "neutral") && Version.TryParse(d.Parts[0], out _))
                .Select(d => (d.Dir, Version: Version.Parse(d.Parts[0])))
                .Where(d => File.Exists(Path.Combine(d.Dir, "winget.exe")))
                .OrderByDescending(d => d.Version)
                .FirstOrDefault();
            if (best.Dir is null)
            {
                log.Warn("elevation", "winget.exe not found under " + windowsApps);
                return null;
            }

            return Path.Combine(best.Dir, "winget.exe");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn("elevation", "cannot enumerate the App Installer package folder", ex);
            return null;
        }
    }
}
