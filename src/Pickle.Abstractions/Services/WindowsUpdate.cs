namespace Pickle.Abstractions.Services;

public sealed record WindowsUpdateInfo(
    string UpdateId,
    string Title,
    string? KbArticle,
    IReadOnlyList<string> Categories,
    long? SizeBytes,
    bool IsDownloaded,
    bool IsMandatory,
    bool IsDriver,
    bool IsOptional,
    string? Severity,
    string? Description,
    DateTimeOffset? ReleaseDate,
    bool RebootMayBeRequired);

public sealed record WindowsUpdateQuery(
    bool IncludeDrivers = true,
    bool IncludeOptional = false,
    bool IncludeHidden = false);

public sealed record WindowsUpdateStatus(
    bool IsSupported,
    bool IsManagedByOrganization,
    string? ManagedReason,
    bool RebootRequired,
    DateTimeOffset? LastSearchSuccess,
    DateTimeOffset? LastInstallSuccess);

public sealed record WindowsUpdateProgress(string Stage, string? CurrentUpdate, double? Percent);

public sealed record WindowsUpdateInstallResult(
    bool Success,
    bool RebootRequired,
    IReadOnlyList<(string UpdateId, string Title, bool Succeeded, string? Error)> Results,
    string Message);

public sealed record WindowsUpdateHistoryEntry(
    string Title,
    DateTimeOffset Date,
    string Operation,
    string Result,
    string? KbArticle);

public interface IWindowsUpdateService
{
    bool IsSupported { get; }

    Task<WindowsUpdateStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Search for applicable, not-installed updates. Does not require elevation.</summary>
    Task<IReadOnlyList<WindowsUpdateInfo>> SearchAsync(WindowsUpdateQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="SearchAsync(WindowsUpdateQuery, CancellationToken)"/> with stage reports. Progress may be reported from
    /// any thread (the Windows Update Agent runs on its own STA thread); cancelling stops waiting even when the agent
    /// can't abort its search.
    /// </summary>
    Task<IReadOnlyList<WindowsUpdateInfo>> SearchAsync(WindowsUpdateQuery query, IProgress<WindowsUpdateProgress>? progress, CancellationToken cancellationToken = default) =>
        SearchAsync(query, cancellationToken);

    /// <summary>Download and install the given update ids. Requires elevation (goes through the broker when not elevated).</summary>
    Task<WindowsUpdateInstallResult> InstallAsync(IReadOnlyList<string> updateIds, IProgress<WindowsUpdateProgress>? progress = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WindowsUpdateHistoryEntry>> GetHistoryAsync(int max = 50, CancellationToken cancellationToken = default);
}
