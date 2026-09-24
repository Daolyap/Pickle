namespace Pickle.Abstractions.Services;

/// <summary>
/// The complete allowlist of operations the elevated helper will perform. The helper never runs arbitrary
/// commands; each kind validates its own arguments (see Pickle.Windows/Elevation/ElevatedOperations.cs).
/// </summary>
public enum ElevatedOperationKind
{
    /// <summary><c>Add-AppxPackage -Path 'https://cdn.winget.microsoft.com/cache/source.msix'</c>. No arguments.</summary>
    WingetRepairSource,

    /// <summary>Upgrade winget packages. Arguments: package ids (validated against a strict id regex), or ["--all"].</summary>
    WingetUpgrade,

    /// <summary>Install winget packages at machine scope. Arguments: package ids.</summary>
    WingetInstall,

    /// <summary>Download and install Windows updates. Arguments: WUA update ids (GUIDs).</summary>
    WindowsUpdateInstall,

    /// <summary>Register a scheduled task that runs with highest privileges. Arguments: [serialized ScheduledTaskDefinition JSON].</summary>
    TaskRegisterElevated,

    /// <summary>Uninstall winget packages. Arguments: package ids (validated like <see cref="WingetUpgrade"/>; no "--all").</summary>
    WingetUninstall,
}

public sealed record ElevatedRequest(ElevatedOperationKind Kind, IReadOnlyList<string> Arguments);

/// <summary>
/// The result of one elevated operation. <c>Output</c> carries extra detail: the full program output for winget and
/// source-repair operations, a JSON summary for <see cref="ElevatedOperationKind.WindowsUpdateInstall"/> (trimmed to
/// fit the helper's message size limit).
/// </summary>
public sealed record ElevatedResponse(bool Success, string Message, int ExitCode = 0, string? Output = null);

public interface IElevationBroker
{
    bool IsSupported { get; }

    /// <summary>True if the current process already has an elevated token (requests then run in-process).</summary>
    bool IsElevated { get; }

    /// <summary>Runs a batch of operations with one UAC prompt. Throws OperationCanceledException if the user declines UAC.</summary>
    Task<IReadOnlyList<ElevatedResponse>> RunAsync(IReadOnlyList<ElevatedRequest> batch, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
