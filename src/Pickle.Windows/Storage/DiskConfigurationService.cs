using System.Globalization;
using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Windows.Elevation;

namespace Pickle.Windows.Storage;

/// <summary>
/// Reads disks like <see cref="DiskLayoutService"/> and changes them through the elevation broker: all the operations of
/// an apply go to the helper as one request (one UAC prompt), which re-checks each against the live disks and stops at the
/// first failure.
/// </summary>
public sealed class DiskConfigurationService(IDiskLayoutService layout, IElevationBroker broker, Func<IPickleShell?> shell) : IDiskConfigurationService
{
    internal const string RangeScript = "param($d, $p) $r = Get-PartitionSupportedSize -DiskNumber $d -PartitionNumber $p -ErrorAction Stop; '{0} {1}' -f $r.SizeMin, $r.SizeMax";

    public bool IsSupported => broker.IsSupported;

    public bool NeedsElevation => !broker.IsElevated;

    public Task<IReadOnlyList<PhysicalDisk>> GetDisksAsync(CancellationToken cancellationToken = default) => layout.GetDisksAsync(cancellationToken);

    public async Task<DiskResizeRange?> GetResizeRangeAsync(int diskNumber, int partitionNumber, CancellationToken cancellationToken = default)
    {
        if (shell() is not { } target)
        {
            return null;
        }

        var result = await target.InvokeAsync(
            RangeScript,
            new Dictionary<string, object?> { ["d"] = diskNumber, ["p"] = partitionNumber },
            ShellTarget.Background,
            cancellationToken).ConfigureAwait(false);
        return result.HadErrors ? null : ParseRange(result.Output.FirstOrDefault()?.ToString());
    }

    public async Task<IReadOnlyList<DiskOperationResult>> ApplyAsync(IReadOnlyList<DiskOperation> operations, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("Disk changes need Windows.");
        }

        foreach (var operation in operations)
        {
            DiskOperationRules.Validate(operation);
        }

        var request = new ElevatedRequest(ElevatedOperationKind.StorageOperations, [DiskOperationCodec.Serialize(operations)]);
        var responses = await broker.RunAsync([request], progress, cancellationToken).ConfigureAwait(false);
        return ParseResults(operations, responses.Count > 0 ? responses[0] : new ElevatedResponse(false, "No answer came back from the elevated helper.", 1));
    }

    internal static DiskResizeRange? ParseRange(string? text)
    {
        var parts = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: 2 }
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var min)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var max)
            && max >= min
            ? new DiskResizeRange(min, max)
            : null;
    }

    /// <summary>One result per operation that ran; when the helper's detail is missing, the overall outcome stands in for the first one.</summary>
    internal static IReadOnlyList<DiskOperationResult> ParseResults(IReadOnlyList<DiskOperation> operations, ElevatedResponse response)
    {
        IReadOnlyList<StorageStepResult>? steps = null;
        try
        {
            steps = response.Output is { Length: > 0 } json ? JsonSerializer.Deserialize<List<StorageStepResult>>(json, PickleJson.Compact) : null;
        }
        catch (JsonException)
        {
        }

        if (steps is null || steps.Count == 0 || steps.Count > operations.Count)
        {
            return [new DiskOperationResult(operations[0], response.Success, response.Message)];
        }

        return [.. steps.Select((step, i) => new DiskOperationResult(operations[i], step.Success, step.Message))];
    }
}
