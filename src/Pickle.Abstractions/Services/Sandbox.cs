namespace Pickle.Abstractions.Services;

/// <summary>A .wsb on/off setting; <see cref="Default"/> leaves it out so Windows Sandbox decides.</summary>
public enum SandboxSwitch
{
    Default,
    Enable,
    Disable,
}

/// <summary>A host folder shared with the sandbox (<c>SandboxFolder</c> null: the sandbox desktop).</summary>
public sealed class SandboxMappedFolder
{
    public string HostFolder { get; set; } = string.Empty;

    public string? SandboxFolder { get; set; }

    public bool ReadOnly { get; set; } = true;
}

/// <summary>
/// A Windows Sandbox configuration: the .wsb settings plus Pickle's customisations, which run as a setup script at
/// logon (theme, Explorer options, winget and packages, Pickle itself, a start page, your own script).
/// </summary>
public sealed class SandboxConfig
{
    public string Name { get; set; } = "Sandbox";

    public string Description { get; set; } = string.Empty;

    public SandboxSwitch Networking { get; set; }

    public SandboxSwitch VGpu { get; set; }

    public SandboxSwitch ClipboardRedirection { get; set; }

    public SandboxSwitch PrinterRedirection { get; set; }

    public SandboxSwitch AudioInput { get; set; }

    public SandboxSwitch VideoInput { get; set; }

    /// <summary>Extra isolation for the remote session (AppContainer); some features stop working.</summary>
    public SandboxSwitch ProtectedClient { get; set; }

    public int? MemoryInMB { get; set; }

    public List<SandboxMappedFolder> MappedFolders { get; set; } = [];

    /// <summary>A command the sandbox runs at logon, after Pickle's setup.</summary>
    public string? LogonCommand { get; set; }

    public bool DarkMode { get; set; }

    public bool ShowFileExtensions { get; set; }

    public bool ShowHiddenFiles { get; set; }

    /// <summary>Install winget in the sandbox (it isn't there by default); needs networking.</summary>
    public bool InstallWinget { get; set; }

    /// <summary>winget package ids to install at logon (implies <see cref="InstallWinget"/>).</summary>
    public List<string> WingetPackages { get; set; } = [];

    /// <summary>Share this Pickle (read-only) and put it on the sandbox's PATH.</summary>
    public bool IncludePickle { get; set; }

    /// <summary>Open Pickle when the sandbox starts (implies <see cref="IncludePickle"/>).</summary>
    public bool StartPickle { get; set; }

    /// <summary>Open this page in Edge when the sandbox starts.</summary>
    public string? StartUrl { get; set; }

    /// <summary>Open the first shared folder in Explorer when the sandbox starts.</summary>
    public bool OpenMappedFolder { get; set; }

    /// <summary>Your own PowerShell, run at the end of setup.</summary>
    public string? SetupScript { get; set; }
}

public sealed record SandboxStatus(bool Supported, bool FeatureEnabled, bool Running, string? Message);

public sealed record SandboxOperationResult(bool Success, string Message, string? Path = null)
{
    public string? Output { get; init; }
}

/// <summary>Windows Sandbox: presets, saved configurations, .wsb export, launch and turning the feature on.</summary>
public interface ISandboxService
{
    bool IsSupported { get; }

    SandboxStatus GetStatus();

    /// <summary>Built-in starting points (safe browsing, test an installer, …).</summary>
    IReadOnlyList<SandboxConfig> Presets { get; }

    IReadOnlyList<SandboxConfig> LoadSaved();

    void Save(SandboxConfig config);

    bool Delete(string name);

    /// <summary>The .wsb XML for a configuration (with the setup script folder it refers to under <paramref name="setupFolder"/>).</summary>
    string BuildWsb(SandboxConfig config, string setupFolder);

    /// <summary>The logon setup script for Pickle's customisations, or null when the configuration needs none.</summary>
    string? BuildSetupScript(SandboxConfig config);

    /// <summary>Problems that would stop the sandbox from starting or its setup from working; empty when fine.</summary>
    IReadOnlyList<string> Validate(SandboxConfig config);

    /// <summary>Writes a .wsb (and its setup folder next to it) you can double-click later.</summary>
    SandboxOperationResult Export(SandboxConfig config, string path);

    Task<SandboxOperationResult> LaunchAsync(SandboxConfig config, CancellationToken cancellationToken = default);

    /// <summary>Turns on the Windows Sandbox feature (administrator; a restart may follow).</summary>
    Task<SandboxOperationResult> EnableFeatureAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
