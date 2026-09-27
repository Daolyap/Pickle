namespace Pickle.Abstractions.Services;

/// <summary>Battery charge (0–100) and whether it is charging; null fields when unknown.</summary>
public sealed record BatteryStatus(int? Percent, bool? Charging, bool OnAcPower);

/// <summary>
/// Facts about the machine the dashboard shows next to the process, network and disk monitors. Every field is
/// optional: what a platform cannot tell is null (load average only exists on Unix, a pending restart on Windows).
/// </summary>
public sealed record SystemSummary(
    string MachineName,
    string UserName,
    string OperatingSystem,
    string? CpuModel,
    int LogicalProcessors,
    TimeSpan Uptime,
    long? SwapUsed,
    long? SwapTotal,
    IReadOnlyList<double>? LoadAverage,
    BatteryStatus? Battery,
    string? RestartPendingReason);

/// <summary>Implemented in Pickle.Core (Windows, Linux, macOS basics); fakes in Pickle.Testing.</summary>
public interface ISystemInfo
{
    Task<SystemSummary> GetSummaryAsync(CancellationToken cancellationToken = default);
}
