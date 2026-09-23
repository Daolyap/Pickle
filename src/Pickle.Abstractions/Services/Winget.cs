namespace Pickle.Abstractions.Services;

public sealed record WingetPackage(
    string Id,
    string Name,
    string? InstalledVersion,
    string? AvailableVersion,
    string? Source,
    string? Publisher = null)
{
    public bool IsInstalled => !string.IsNullOrEmpty(InstalledVersion);

    public bool IsUpgradable => IsInstalled && !string.IsNullOrEmpty(AvailableVersion)
        && !string.Equals(InstalledVersion, AvailableVersion, StringComparison.OrdinalIgnoreCase);
}

public sealed record WingetPackageDetails(
    string Id,
    string Name,
    string? Publisher,
    string? Description,
    string? Homepage,
    string? License,
    string? LatestVersion,
    IReadOnlyList<string> AvailableVersions,
    string? ReleaseNotes = null);

public sealed record WingetSource(string Name, string Argument, string Type);

public sealed record WingetProgress(string Stage, double? Percent = null, string? Message = null);

public sealed record WingetOperationResult(bool Success, string Message, int? ExitCode = null, bool RebootRequired = false);

public enum WingetScope
{
    Any,
    User,
    Machine,
}

public sealed record WingetInstallOptions(
    string? Version = null,
    WingetScope Scope = WingetScope.Any,
    bool Silent = true,
    bool Force = false,
    bool AcceptAgreements = true,
    bool IncludeUnknown = false);

public enum WingetBackend
{
    Unavailable,

    /// <summary>Microsoft.WinGet.Client PowerShell module (structured objects).</summary>
    PowerShellModule,

    /// <summary>Parsing winget.exe text output.</summary>
    Cli,
}

public interface IWingetService
{
    bool IsSupported { get; }

    Task<WingetBackend> GetBackendAsync(CancellationToken cancellationToken = default);

    /// <summary>Install Microsoft.WinGet.Client from PSGallery (CurrentUser scope).</summary>
    Task<WingetOperationResult> InstallClientModuleAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WingetPackage>> ListInstalledAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WingetPackage>> ListUpgradesAsync(bool includeUnknown = false, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WingetPackage>> SearchAsync(string query, CancellationToken cancellationToken = default);

    Task<WingetPackageDetails?> GetDetailsAsync(string id, CancellationToken cancellationToken = default);

    Task<WingetOperationResult> InstallAsync(string id, WingetInstallOptions options, IProgress<WingetProgress>? progress = null, CancellationToken cancellationToken = default);

    Task<WingetOperationResult> UpgradeAsync(string id, WingetInstallOptions options, IProgress<WingetProgress>? progress = null, CancellationToken cancellationToken = default);

    Task<WingetOperationResult> UninstallAsync(string id, IProgress<WingetProgress>? progress = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WingetSource>> ListSourcesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-registers the winget source package via
    /// <c>Add-AppxPackage -Path 'https://cdn.winget.microsoft.com/cache/source.msix'</c>. When <paramref name="elevated"/>
    /// is true this runs through the elevation broker (fixes winget sources for admin sessions).
    /// </summary>
    Task<WingetOperationResult> RepairSourceAsync(bool elevated, CancellationToken cancellationToken = default);
}
