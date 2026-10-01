namespace Pickle.Abstractions.Services;

public enum ServiceState
{
    Unknown,
    Running,
    Stopped,
    Starting,
    Stopping,
    Paused,
    Failed,
}

public enum ServiceStartMode
{
    Unknown,
    Automatic,
    Manual,
    Disabled,
}

public enum ServiceAction
{
    Start,
    Stop,
    Restart,

    /// <summary>Start at boot (systemd enable, Windows Automatic).</summary>
    Enable,

    /// <summary>Do not start at boot (systemd disable, Windows Manual).</summary>
    Disable,
}

/// <summary>A Windows service, systemd unit or launchd job.</summary>
public sealed record ServiceInfo(
    string Id,
    string DisplayName,
    ServiceState State,
    ServiceStartMode StartMode,
    string? Description = null,
    string? Account = null,
    string? Path = null)
{
    public bool IsRunning => State is ServiceState.Running or ServiceState.Starting;
}

public sealed record ServiceOperationResult(bool Success, string Message)
{
    /// <summary>Needs a password on a terminal: run <see cref="ShellCommand"/> in the shell.</summary>
    public string? ShellCommand { get; init; }
}

/// <summary>Services, daemons and jobs of the local machine. Implemented per OS (SCM, systemd, launchd).</summary>
public interface ISystemServiceManager
{
    /// <summary>"Windows services", "systemd", "launchd".</summary>
    string Name { get; }

    bool IsSupported { get; }

    /// <summary>Per-user services (systemd <c>--user</c>) as a second scope; false where there is none.</summary>
    bool HasUserScope { get; }

    Task<IReadOnlyList<ServiceInfo>> ListAsync(bool userScope, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> DetailsAsync(string id, bool userScope, CancellationToken cancellationToken = default);

    /// <summary>The PowerShell command that follows the service's log (<c>journalctl -fu x</c>, <c>Get-WinEvent …</c>), or null.</summary>
    string? LogsCommand(string id, bool userScope);

    Task<ServiceOperationResult> ControlAsync(string id, ServiceAction action, bool userScope, CancellationToken cancellationToken = default);
}
