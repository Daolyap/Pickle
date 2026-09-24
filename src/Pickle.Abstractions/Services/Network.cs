namespace Pickle.Abstractions.Services;

/// <summary>
/// A network interface with cumulative byte counters and the rates since the previous sample (bytes per second).
/// <see cref="Speed"/> is the link speed in bits per second, null when unknown.
/// </summary>
public sealed record NetworkInterfaceSample(
    string Id,
    string Name,
    string Description,
    string Type,
    string Status,
    IReadOnlyList<string> IPv4,
    IReadOnlyList<string> IPv6,
    string? Mac,
    long? Speed,
    long BytesSent,
    long BytesReceived,
    double SendRate,
    double ReceiveRate);

/// <summary>A TCP connection or a TCP/UDP listener. <see cref="ProcessId"/> is null when the owner is unknown.</summary>
public sealed record NetworkConnection(
    string Protocol,
    string LocalAddress,
    int LocalPort,
    string? RemoteAddress,
    int? RemotePort,
    string State,
    int? ProcessId = null,
    string? ProcessName = null);

public sealed record PingResult(bool Success, string Status, string? Address, long RoundTripMs, int? Ttl);

public sealed record NetworkToolResult(bool Success, string Output);

/// <summary>Interfaces, connections and tools for the Network panel (Alt+N). Implemented in Pickle.Core.</summary>
public interface INetworkMonitor
{
    /// <summary>Interfaces with send/receive rates measured since the previous call (0 on the first).</summary>
    Task<IReadOnlyList<NetworkInterfaceSample>> SampleInterfacesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NetworkConnection>> GetConnectionsAsync(CancellationToken cancellationToken = default);

    Task<PingResult> PingAsync(string host, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>The addresses <paramref name="host"/> resolves to.</summary>
    Task<IReadOnlyList<string>> LookupAsync(string host, CancellationToken cancellationToken = default);

    /// <summary>True on Windows (<c>ipconfig /flushdns</c>).</summary>
    bool CanFlushDns { get; }

    Task<NetworkToolResult> FlushDnsAsync(CancellationToken cancellationToken = default);
}
