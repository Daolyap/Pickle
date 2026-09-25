using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Sandbox;

/// <summary>
/// Windows Sandbox. Saved configurations live in <c>&lt;config&gt;/sandboxes/*.json</c> (synced with the rest of the
/// config); a launch writes the .wsb and setup script to <c>&lt;data&gt;/sandbox/run/&lt;name&gt;</c> and starts
/// System32\WindowsSandbox.exe on it.
/// </summary>
public sealed class WindowsSandboxService : ISandboxService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly string[] SandboxProcesses = ["WindowsSandboxRemoteSession", "WindowsSandboxClient", "WindowsSandboxServer"];

    private readonly PicklePaths _paths;
    private readonly Func<IElevationBroker?> _broker;
    private readonly Func<string> _cwd;
    private readonly Func<SandboxStatus> _status;
    private readonly Func<string, string, bool> _start;
    private readonly Func<string?> _pickleFolder;

    public WindowsSandboxService(IPickleContext context)
        : this(
            context.Paths,
            () => context.Services.Get<IElevationBroker>(),
            () => context.Shell.CurrentDirectory,
            DetectStatus,
            StartSandbox,
            CurrentPickleFolder,
            OperatingSystem.IsWindows())
    {
    }

    internal WindowsSandboxService(
        PicklePaths paths,
        Func<IElevationBroker?> broker,
        Func<string> cwd,
        Func<SandboxStatus> status,
        Func<string, string, bool> start,
        Func<string?> pickleFolder,
        bool isSupported)
    {
        _paths = paths;
        _broker = broker;
        _cwd = cwd;
        _status = status;
        _start = start;
        _pickleFolder = pickleFolder;
        IsSupported = isSupported;
    }

    public bool IsSupported { get; }

    public string ConfigDirectory => Path.Combine(_paths.ConfigDir, "sandboxes");

    public string RunDirectory => Path.Combine(_paths.DataDir, "sandbox", "run");

    public IReadOnlyList<SandboxConfig> Presets => SandboxPresets.Create(DownloadsFolder(), _cwd());

    public SandboxStatus GetStatus() =>
        IsSupported ? _status() : new SandboxStatus(false, false, false, "Windows Sandbox is only available on Windows.");

    public IReadOnlyList<SandboxConfig> LoadSaved()
    {
        if (!Directory.Exists(ConfigDirectory))
        {
            return [];
        }

        var configs = new List<SandboxConfig>();
        foreach (var file in Directory.EnumerateFiles(ConfigDirectory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (JsonSerializer.Deserialize<SandboxConfig>(File.ReadAllText(file), PickleJson.Options) is { } config && SandboxNames.IsValid(config.Name))
                {
                    configs.Add(config);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return configs;
    }

    public void Save(SandboxConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!SandboxNames.IsValid(config.Name))
        {
            throw new ArgumentException("Name: letters, digits, spaces, '-' and '_' only (at most 60).");
        }

        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(Path.Combine(ConfigDirectory, SandboxNames.FileName(config.Name)), JsonSerializer.Serialize(config, PickleJson.Options), Utf8NoBom);
    }

    public bool Delete(string name)
    {
        if (!SandboxNames.IsValid(name))
        {
            return false;
        }

        var file = Path.Combine(ConfigDirectory, SandboxNames.FileName(name));
        if (!File.Exists(file))
        {
            return false;
        }

        File.Delete(file);
        return true;
    }

    public string BuildWsb(SandboxConfig config, string setupFolder) => WsbBuilder.BuildXml(config, setupFolder, _pickleFolder());

    public string? BuildSetupScript(SandboxConfig config) => WsbBuilder.NeedsSetup(config) ? WsbBuilder.BuildSetupScript(config) : null;

    public IReadOnlyList<string> Validate(SandboxConfig config) => WsbBuilder.Validate(config);

    public SandboxOperationResult Export(SandboxConfig config, string path)
    {
        if (WsbBuilder.Validate(config) is { Count: > 0 } errors)
        {
            return new SandboxOperationResult(false, string.Join(" ", errors));
        }

        var full = Path.GetFullPath(path.EndsWith(".wsb", StringComparison.OrdinalIgnoreCase) ? path : path + ".wsb");
        var directory = Path.GetDirectoryName(full)!;
        var setupFolder = Path.Combine(directory, Path.GetFileNameWithoutExtension(full) + ".setup");
        Write(config, full, setupFolder);
        return new SandboxOperationResult(true, $"Wrote {full}" + (WsbBuilder.NeedsSetup(config) ? $" (setup script in {setupFolder})" : string.Empty), full);
    }

    public Task<SandboxOperationResult> LaunchAsync(SandboxConfig config, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return Result(false, "Windows Sandbox is only available on Windows.");
        }

        if (WsbBuilder.Validate(config) is { Count: > 0 } errors)
        {
            return Result(false, string.Join(" ", errors));
        }

        var status = _status();
        if (!status.FeatureEnabled)
        {
            return Result(false, status.Message ?? "Windows Sandbox is turned off. Turn it on with 'pk sandbox enable' (administrator, then restart).");
        }

        if (status.Running)
        {
            return Result(false, "A sandbox is already running; Windows runs one at a time. Close it first.");
        }

        var folder = Path.Combine(RunDirectory, config.Name);
        var wsb = Path.Combine(folder, config.Name + ".wsb");
        Write(config, wsb, Path.Combine(folder, "setup"));
        return _start(wsb, folder)
            ? Result(true, $"Starting sandbox '{config.Name}'…", wsb)
            : Result(false, "Could not start Windows Sandbox.", wsb);
    }

    public async Task<SandboxOperationResult> EnableFeatureAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return new SandboxOperationResult(false, "Windows Sandbox is only available on Windows.");
        }

        if (_broker() is not { IsSupported: true } broker)
        {
            return new SandboxOperationResult(false, "Administrator rights are needed, but the elevation helper isn't available.");
        }

        try
        {
            var responses = await broker.RunAsync([new ElevatedRequest(ElevatedOperationKind.EnableWindowsSandbox, [])], progress, cancellationToken).ConfigureAwait(false);
            var response = responses.FirstOrDefault() ?? new ElevatedResponse(false, "The elevated helper returned nothing.", 1);
            return new SandboxOperationResult(response.Success, response.Message) { Output = response.Output };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SandboxOperationResult(false, "Cancelled at the administrator prompt.");
        }
    }

    private void Write(SandboxConfig config, string wsbPath, string setupFolder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(wsbPath)!);
        if (WsbBuilder.NeedsSetup(config))
        {
            Directory.CreateDirectory(setupFolder);
            File.WriteAllText(Path.Combine(setupFolder, WsbBuilder.SetupScriptName), WsbBuilder.BuildSetupScript(config), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        File.WriteAllText(wsbPath, BuildWsb(config, setupFolder), Utf8NoBom);
    }

    private static Task<SandboxOperationResult> Result(bool success, string message, string? path = null) =>
        Task.FromResult(new SandboxOperationResult(success, message, path));

    private static string? DownloadsFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");
        return profile.Length > 0 && Directory.Exists(downloads) ? downloads : null;
    }

    private static string? CurrentPickleFolder()
    {
        var process = Environment.ProcessPath;
        return process is not null && Path.GetFileNameWithoutExtension(process).Equals("pickle", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(process)
            : null;
    }

    private static SandboxStatus DetectStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new SandboxStatus(false, false, false, "Windows Sandbox is only available on Windows.");
        }

        var edition = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", null) as string ?? string.Empty;
        var enabled = File.Exists(Path.Combine(Environment.SystemDirectory, "WindowsSandbox.exe"));
        var running = SandboxProcesses.Any(name =>
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var p in processes)
            {
                p.Dispose();
            }

            return processes.Length > 0;
        });
        if (!enabled && edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase))
        {
            return new SandboxStatus(false, false, false, "Windows Sandbox needs Windows Pro, Enterprise or Education (this is Windows Home).");
        }

        return new SandboxStatus(true, enabled, running, enabled ? null : "Windows Sandbox is turned off. Turn it on with 'pk sandbox enable' (administrator, then restart).");
    }

    private static bool StartSandbox(string wsbPath, string workingDirectory)
    {
        var exe = Path.Combine(Environment.SystemDirectory, "WindowsSandbox.exe");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = workingDirectory };
        start.ArgumentList.Add(wsbPath);
        try
        {
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
