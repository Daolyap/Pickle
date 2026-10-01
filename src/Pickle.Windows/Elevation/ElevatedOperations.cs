using Pickle.Abstractions.Services;
using Pickle.Windows.TaskScheduler;

namespace Pickle.Windows.Elevation;

/// <summary>A request that passed <see cref="ElevatedOperations.Validate"/>; only these reach an executor.</summary>
internal sealed record ValidatedOperation(ElevatedOperationKind Kind, IReadOnlyList<string> Ids, bool All = false, ScheduledTaskDefinition? Task = null)
{
    public IReadOnlyList<DiskOperation>? Disk { get; init; }
}

/// <summary>What the helper can actually do. The real implementation runs fixed commands only (see WindowsElevatedExecutor).</summary>
internal interface IElevatedExecutor
{
    Task<ElevatedResponse> RepairWingetSourceAsync(IProgress<string> progress, CancellationToken cancellationToken);

    /// <summary>Whether the account the helper runs as has winget's source package (an admin account may never have run winget).</summary>
    Task<bool> HasWingetSourceAsync(CancellationToken cancellationToken);

    /// <summary>Runs winget.exe with arguments built by <see cref="ElevatedOperations.BuildWingetArguments"/>.</summary>
    Task<ElevatedResponse> RunWingetAsync(IReadOnlyList<string> arguments, IProgress<string> progress, CancellationToken cancellationToken);

    Task<ElevatedResponse> InstallWindowsUpdatesAsync(IReadOnlyList<string> updateIds, IProgress<string> progress, CancellationToken cancellationToken);

    Task<ElevatedResponse> RegisterTaskAsync(ScheduledTaskDefinition definition, IProgress<string> progress, CancellationToken cancellationToken);

    /// <summary>Runs System32 dism.exe with <see cref="ElevatedOperations.EnableSandboxArguments"/>.</summary>
    Task<ElevatedResponse> EnableWindowsSandboxAsync(IProgress<string> progress, CancellationToken cancellationToken);

    /// <summary><paramref name="action"/> is one of <see cref="ElevatedOperations.ServiceActions"/>.</summary>
    Task<ElevatedResponse> ControlServiceAsync(string serviceName, string action, IProgress<string> progress, CancellationToken cancellationToken);

    Task<ElevatedResponse> WriteHostsFileAsync(string content, IProgress<string> progress, CancellationToken cancellationToken);

    /// <summary>A null <paramref name="value"/> removes the variable.</summary>
    Task<ElevatedResponse> SetMachineEnvironmentAsync(string name, string? value, IProgress<string> progress, CancellationToken cancellationToken);

    /// <summary>Runs already validated disk operations in order, re-checking each against the live disks and stopping at the first failure.</summary>
    Task<ElevatedResponse> RunStorageOperationsAsync(IReadOnlyList<DiskOperation> operations, IProgress<string> progress, CancellationToken cancellationToken);
}

/// <summary>The allowlist: strict per-kind argument validation and dispatch to an <see cref="IElevatedExecutor"/>.</summary>
internal static class ElevatedOperations
{
    public const int MaxArguments = 64;
    public const int MaxArgumentLength = TaskDefinitionCodec.MaxJsonLength;
    public const string AllPackages = "--all";

    /// <summary>What <see cref="ElevatedOperationKind.ServiceControl"/> accepts as its second argument.</summary>
    public static readonly IReadOnlyList<string> ServiceActions = ["start", "stop", "restart", "automatic", "manual", "disabled"];

    private static readonly string[] WingetCommon =
        ["--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"];

    /// <summary>The only dism command the helper runs.</summary>
    public static readonly IReadOnlyList<string> EnableSandboxArguments =
        ["/Online", "/Enable-Feature", "/FeatureName:Containers-DisposableClientVM", "/All", "/NoRestart", "/Quiet"];

    // winget uninstall has no package agreements to accept.
    private static readonly string[] WingetUninstallCommon = ["--silent", "--accept-source-agreements", "--disable-interactivity"];

    /// <summary>Throws <see cref="ArgumentException"/> for anything outside the allowlist.</summary>
    public static ValidatedOperation Validate(ElevatedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Kind))
        {
            throw new ArgumentException($"Operation {(int)request.Kind} is not allowed.");
        }

        var args = request.Arguments ?? throw new ArgumentException("Arguments are missing.");
        if (args.Count > MaxArguments)
        {
            throw new ArgumentException($"Too many arguments ({args.Count}).");
        }

        if (args.Any(a => a is null || a.Length == 0 || a.Length > MaxArgumentLength))
        {
            throw new ArgumentException("An argument is empty or too long.");
        }

        switch (request.Kind)
        {
            case ElevatedOperationKind.WingetRepairSource or ElevatedOperationKind.EnableWindowsSandbox:
                return args.Count == 0
                    ? new ValidatedOperation(request.Kind, [])
                    : throw new ArgumentException($"{request.Kind} takes no arguments.");

            case ElevatedOperationKind.WingetUpgrade when args.Count == 1 && args[0] == AllPackages:
                return new ValidatedOperation(request.Kind, [], All: true);

            case ElevatedOperationKind.WingetUpgrade:
            case ElevatedOperationKind.WingetInstall:
            case ElevatedOperationKind.WingetUninstall:
                if (args.Count == 0)
                {
                    throw new ArgumentException($"{request.Kind} needs at least one package id.");
                }

                foreach (var id in args)
                {
                    if (!WindowsIds.IsValidWingetId(id))
                    {
                        throw new ArgumentException($"'{id}' is not a valid winget package id.");
                    }
                }

                return new ValidatedOperation(request.Kind, [.. args.Distinct(StringComparer.OrdinalIgnoreCase)]);

            case ElevatedOperationKind.WindowsUpdateInstall:
                if (args.Count == 0)
                {
                    throw new ArgumentException("WindowsUpdateInstall needs at least one update id.");
                }

                var ids = new List<string>();
                foreach (var value in args)
                {
                    if (!WindowsIds.TryNormalizeUpdateId(value, out var id))
                    {
                        throw new ArgumentException($"'{value}' is not a valid update id (GUID).");
                    }

                    if (!ids.Contains(id))
                    {
                        ids.Add(id);
                    }
                }

                return new ValidatedOperation(request.Kind, ids);

            case ElevatedOperationKind.TaskRegisterElevated:
                if (args.Count != 1)
                {
                    throw new ArgumentException("TaskRegisterElevated takes exactly one task definition.");
                }

                var definition = TaskDefinitionCodec.Deserialize(args[0]);
                TaskDefinitionCodec.Validate(definition, elevated: true);
                return new ValidatedOperation(request.Kind, [], Task: definition with { RunElevated = true });

            case ElevatedOperationKind.ServiceControl:
                if (args.Count != 2 || !WindowsIds.IsValidServiceName(args[0]))
                {
                    throw new ArgumentException("ServiceControl takes a service name and an action.");
                }

                return ServiceActions.Contains(args[1], StringComparer.Ordinal)
                    ? new ValidatedOperation(request.Kind, [args[0], args[1]])
                    : throw new ArgumentException($"'{args[1]}' is not a service action ({string.Join(", ", ServiceActions)}).");

            case ElevatedOperationKind.HostsFileWrite:
                if (args.Count != 1)
                {
                    throw new ArgumentException("HostsFileWrite takes the complete new hosts file.");
                }

                HostsDocument.Validate(args[0]);
                return new ValidatedOperation(request.Kind, [args[0]]);

            case ElevatedOperationKind.MachineEnvironmentSet:
                if (args.Count is < 1 or > 2)
                {
                    throw new ArgumentException("MachineEnvironmentSet takes a name and a value.");
                }

                var variableValue = args.Count == 2 ? args[1] : null;
                EnvironmentRules.Validate(args[0], variableValue, machineScope: true);
                return new ValidatedOperation(request.Kind, variableValue is null ? [args[0]] : [args[0], variableValue]);

            case ElevatedOperationKind.StorageOperations:
                if (args.Count != 1)
                {
                    throw new ArgumentException("StorageOperations takes one JSON array of disk operations.");
                }

                var disk = DiskOperationCodec.Deserialize(args[0]);
                return new ValidatedOperation(request.Kind, []) { Disk = disk };

            default:
                throw new ArgumentException($"Operation {request.Kind} is not allowed.");
        }
    }

    public static IReadOnlyList<string> BuildWingetArguments(ElevatedOperationKind kind, string? id)
    {
        return kind switch
        {
            ElevatedOperationKind.WingetUpgrade when id is null => ["upgrade", AllPackages, .. WingetCommon],
            ElevatedOperationKind.WingetUpgrade => ["upgrade", "--id", Checked(id), "--exact", .. WingetCommon],
            ElevatedOperationKind.WingetInstall when id is not null => ["install", "--id", Checked(id), "--exact", "--scope", "machine", .. WingetCommon],
            ElevatedOperationKind.WingetUninstall when id is not null => ["uninstall", "--id", Checked(id), "--exact", .. WingetUninstallCommon],
            _ => throw new ArgumentException($"{kind} does not run winget with a package id."),
        };

        static string Checked(string value) =>
            WindowsIds.IsValidWingetId(value) ? value : throw new ArgumentException($"'{value}' is not a valid winget package id.");
    }

    private static string Verb(ElevatedOperationKind kind) => kind switch
    {
        ElevatedOperationKind.WingetInstall => "Installing",
        ElevatedOperationKind.WingetUninstall => "Uninstalling",
        _ => "Upgrading",
    };

    public static TimeSpan TimeoutFor(ElevatedOperationKind kind) => kind switch
    {
        ElevatedOperationKind.WingetRepairSource => TimeSpan.FromMinutes(10),
        ElevatedOperationKind.WingetUpgrade or ElevatedOperationKind.WingetInstall or ElevatedOperationKind.WingetUninstall => TimeSpan.FromHours(2),
        ElevatedOperationKind.WindowsUpdateInstall => TimeSpan.FromHours(3),
        ElevatedOperationKind.TaskRegisterElevated => TimeSpan.FromMinutes(2),
        ElevatedOperationKind.EnableWindowsSandbox => TimeSpan.FromMinutes(20),
        ElevatedOperationKind.ServiceControl => TimeSpan.FromMinutes(3),
        ElevatedOperationKind.StorageOperations => TimeSpan.FromHours(2),
        _ => TimeSpan.FromMinutes(1),
    };

    /// <summary>Runs a validated operation. Never throws for operation failures; they become unsuccessful responses.</summary>
    public static async Task<ElevatedResponse> ExecuteAsync(ValidatedOperation operation, IElevatedExecutor executor, IProgress<string> progress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeoutFor(operation.Kind));
        try
        {
            switch (operation.Kind)
            {
                case ElevatedOperationKind.WingetRepairSource:
                    return await executor.RepairWingetSourceAsync(progress, timeout.Token).ConfigureAwait(false);

                case ElevatedOperationKind.WingetUpgrade when operation.All:
                    await EnsureWingetSourceAsync(executor, progress, timeout.Token).ConfigureAwait(false);
                    return await executor.RunWingetAsync(BuildWingetArguments(operation.Kind, null), progress, timeout.Token).ConfigureAwait(false);

                case ElevatedOperationKind.WingetUpgrade:
                case ElevatedOperationKind.WingetInstall:
                case ElevatedOperationKind.WingetUninstall:
                    await EnsureWingetSourceAsync(executor, progress, timeout.Token).ConfigureAwait(false);
                    var responses = new List<(string Id, ElevatedResponse Response)>();
                    foreach (var id in operation.Ids)
                    {
                        progress.Report($"{Verb(operation.Kind)} {id}…");
                        responses.Add((id, await executor.RunWingetAsync(BuildWingetArguments(operation.Kind, id), progress, timeout.Token).ConfigureAwait(false)));
                    }

                    var failed = responses.Select(r => r.Response).FirstOrDefault(r => !r.Success);
                    var output = string.Join(
                        Environment.NewLine,
                        responses.Where(r => !string.IsNullOrWhiteSpace(r.Response.Output)).Select(r => $"── {r.Id} ──{Environment.NewLine}{r.Response.Output!.TrimEnd()}"));
                    return new ElevatedResponse(
                        failed is null,
                        string.Join(Environment.NewLine, responses.Select(r => r.Response.Message)),
                        failed?.ExitCode ?? 0,
                        output.Length == 0 ? null : output);

                case ElevatedOperationKind.WindowsUpdateInstall:
                    return await executor.InstallWindowsUpdatesAsync(operation.Ids, progress, timeout.Token).ConfigureAwait(false);

                case ElevatedOperationKind.TaskRegisterElevated when operation.Task is not null:
                    return await executor.RegisterTaskAsync(operation.Task, progress, timeout.Token).ConfigureAwait(false);

                case ElevatedOperationKind.EnableWindowsSandbox:
                    return await executor.EnableWindowsSandboxAsync(progress, timeout.Token).ConfigureAwait(false);

                case ElevatedOperationKind.ServiceControl when operation.Ids.Count == 2:
                    return await executor.ControlServiceAsync(operation.Ids[0], operation.Ids[1], progress, timeout.Token).ConfigureAwait(false);

                case ElevatedOperationKind.HostsFileWrite when operation.Ids.Count == 1:
                    return await executor.WriteHostsFileAsync(operation.Ids[0], progress, timeout.Token).ConfigureAwait(false);

                case ElevatedOperationKind.MachineEnvironmentSet when operation.Ids.Count is 1 or 2:
                    return await executor.SetMachineEnvironmentAsync(operation.Ids[0], operation.Ids.Count == 2 ? operation.Ids[1] : null, progress, timeout.Token).ConfigureAwait(false);

                case ElevatedOperationKind.StorageOperations when operation.Disk is { Count: > 0 } disk:
                    return await executor.RunStorageOperationsAsync(disk, progress, timeout.Token).ConfigureAwait(false);

                default:
                    return new ElevatedResponse(false, $"Operation {operation.Kind} is not allowed.", 1);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ElevatedResponse(false, $"{operation.Kind} timed out.", 1460);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return new ElevatedResponse(false, $"{operation.Kind} failed: {ex.Message}", ex.HResult);
        }
    }

    // winget run as another (administrator) account fails with "no sources" until the source package is registered
    // there; do that once, in the same UAC session, rather than leaving it to a separate "Repair as admin".
    private static async Task EnsureWingetSourceAsync(IElevatedExecutor executor, IProgress<string> progress, CancellationToken cancellationToken)
    {
        if (await executor.HasWingetSourceAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        progress.Report("Adding the winget source for the administrator account…");
        var repair = await executor.RepairWingetSourceAsync(progress, cancellationToken).ConfigureAwait(false);
        if (!repair.Success)
        {
            progress.Report("Adding the winget source failed: " + repair.Message);
        }
    }
}
