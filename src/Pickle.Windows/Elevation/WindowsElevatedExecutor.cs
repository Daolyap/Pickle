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
internal sealed partial class WindowsElevatedExecutor(IPickleLogger log, IProcessRunner? runner = null) : IElevatedExecutor
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

    public async Task<bool> HasWingetSourceAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            WingetService.WindowsPowerShellPath,
            WingetSourceRepair.CheckArguments(),
            null,
            TimeSpan.FromMinutes(1),
            cancellationToken,
            WingetSourceRepair.Environment()).ConfigureAwait(false);
        return result.ExitCode == 0 && !result.TimedOut;
    }

    public async Task<ElevatedResponse> EnableWindowsSandboxAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        progress.Report("Turning on Windows Sandbox (this can take a few minutes)…");
        var dism = Path.Combine(Environment.SystemDirectory, "dism.exe");
        var result = await _runner.RunAsync(dism, ElevatedOperations.EnableSandboxArguments, null, ElevatedOperations.TimeoutFor(ElevatedOperationKind.EnableWindowsSandbox), cancellationToken).ConfigureAwait(false);

        // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED.
        return result.ExitCode switch
        {
            0 => new ElevatedResponse(true, "Windows Sandbox is turned on.", 0, result.Output),
            3010 => new ElevatedResponse(true, "Windows Sandbox is turned on. Restart your PC to finish.", 3010, result.Output),
            _ when result.TimedOut => new ElevatedResponse(false, "Turning on Windows Sandbox timed out.", 1460, result.Output),
            var code => new ElevatedResponse(false, $"dism failed ({code}). Windows Sandbox needs Windows Pro, Enterprise or Education and virtualization enabled in the firmware.", code, result.Output),
        };
    }

    // The service name and action travel as environment variables, never inside the script text.
    internal const string ServiceScript = """
        $ErrorActionPreference = 'Stop'
        $name = $env:PICKLE_SERVICE
        switch ($env:PICKLE_ACTION) {
            'start' { Start-Service -Name $name }
            'stop' { Stop-Service -Name $name }
            'restart' { Restart-Service -Name $name }
            'automatic' { Set-Service -Name $name -StartupType Automatic }
            'manual' { Set-Service -Name $name -StartupType Manual }
            'disabled' { Set-Service -Name $name -StartupType Disabled }
            default { throw 'Unknown action.' }
        }
        'OK'
        """;

    public async Task<ElevatedResponse> ControlServiceAsync(string serviceName, string action, IProgress<string> progress, CancellationToken cancellationToken)
    {
        progress.Report($"{action} {serviceName} (elevated)…");
        var environment = new Dictionary<string, string?>(WingetSourceRepair.Environment())
        {
            ["PICKLE_SERVICE"] = serviceName,
            ["PICKLE_ACTION"] = action,
        };
        var result = await _runner.RunAsync(
            WingetService.WindowsPowerShellPath,
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(ServiceScript))],
            null,
            ElevatedOperations.TimeoutFor(ElevatedOperationKind.ServiceControl),
            cancellationToken,
            environment).ConfigureAwait(false);
        var text = result.Output.Trim();
        return result.ExitCode == 0 && !result.TimedOut
            ? new ElevatedResponse(true, $"{serviceName}: {action} done.", 0, text)
            : new ElevatedResponse(false, result.TimedOut ? $"{serviceName}: {action} timed out." : FirstLine(text, $"{serviceName}: {action} failed ({result.ExitCode})."), result.ExitCode, text);
    }

    public Task<ElevatedResponse> WriteHostsFileAsync(string content, IProgress<string> progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            HostsDocument.Validate(content);
            var path = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
            progress.Report("Writing " + path + " (elevated)…");
            try
            {
                if (File.Exists(path))
                {
                    File.Copy(path, path + ".pickle-backup", overwrite: true);
                }

                // In place, so the file keeps its own permissions.
                File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
                return new ElevatedResponse(true, "The hosts file was saved (the previous one is hosts.pickle-backup).");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new ElevatedResponse(false, "Writing the hosts file failed: " + ex.Message, ex.HResult);
            }
        }, cancellationToken);

    public Task<ElevatedResponse> SetMachineEnvironmentAsync(string name, string? value, IProgress<string> progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            EnvironmentRules.Validate(name, value, machineScope: true);
            progress.Report($"{(value is null ? "Removing" : "Setting")} {name} for all users (elevated)…");
            try
            {
                MachineEnvironment.Set(name, value);
                return new ElevatedResponse(true, value is null ? $"{name} was removed." : $"{name} was saved. New programs see it; running ones keep the old value.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return new ElevatedResponse(false, $"Changing {name} failed: {ex.Message}", ex.HResult);
            }
        }, cancellationToken);

    private static string FirstLine(string text, string fallback) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? fallback;

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
