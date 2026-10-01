using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Windows.Storage;
using Pickle.Windows.Winget;

namespace Pickle.Windows.Elevation;

/// <summary>What one disk operation did; the elevated helper sends these back as <see cref="ElevatedResponse.Output"/>.</summary>
internal sealed record StorageStepResult(bool Success, string Message);

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsElevatedExecutor
{
    // Everything that varies travels as an environment variable, never inside the script text. The operations were
    // validated by DiskOperationRules, and each one is checked again against the live disks before this runs.
    internal const string StorageScript = """
        $ErrorActionPreference = 'Stop'
        $disk = [int]$env:PICKLE_DISK
        $part = if ($env:PICKLE_PARTITION) { [int]$env:PICKLE_PARTITION } else { 0 }
        $label = $env:PICKLE_LABEL
        function Get-Target { Get-Partition -DiskNumber $disk -PartitionNumber $part }
        switch ($env:PICKLE_DISK_OP) {
            'InitializeDisk' { Initialize-Disk -Number $disk -PartitionStyle $env:PICKLE_OPTION -Confirm:$false }
            'NewVolume' {
                $new = @{ DiskNumber = $disk }
                if ($env:PICKLE_SIZE) { $new.Size = [uint64]$env:PICKLE_SIZE } else { $new.UseMaximumSize = $true }
                if ($env:PICKLE_LETTER) { $new.DriveLetter = [char]$env:PICKLE_LETTER } else { $new.AssignDriveLetter = $true }
                $created = New-Partition @new
                if ($env:PICKLE_FS) {
                    $format = @{ FileSystem = $env:PICKLE_FS; Confirm = $false }
                    if ($label) { $format.NewFileSystemLabel = $label }
                    $created | Format-Volume @format | Out-Null
                }
            }
            'FormatVolume' {
                $format = @{ FileSystem = $env:PICKLE_FS; Confirm = $false }
                if ($label) { $format.NewFileSystemLabel = $label }
                Get-Target | Format-Volume @format | Out-Null
            }
            'ResizePartition' {
                $size = [uint64]$env:PICKLE_SIZE
                $range = Get-PartitionSupportedSize -DiskNumber $disk -PartitionNumber $part
                if ($size -lt $range.SizeMin -or $size -gt $range.SizeMax) { throw "Windows allows between $($range.SizeMin) and $($range.SizeMax) bytes for this partition." }
                Resize-Partition -DiskNumber $disk -PartitionNumber $part -Size $size
            }
            'DeletePartition' { Remove-Partition -DiskNumber $disk -PartitionNumber $part -Confirm:$false }
            'SetDriveLetter' { Set-Partition -DiskNumber $disk -PartitionNumber $part -NewDriveLetter ([char]$env:PICKLE_LETTER) }
            'SetLabel' { Get-Target | Get-Volume | Set-Volume -NewFileSystemLabel $label }
            'CheckVolume' {
                $volume = Get-Target | Get-Volume
                $result = switch ($env:PICKLE_OPTION) {
                    'scan' { Repair-Volume -InputObject $volume -Scan }
                    'spotfix' { Repair-Volume -InputObject $volume -SpotFix }
                    'fix' { Repair-Volume -InputObject $volume -OfflineScanAndFix }
                    default { throw 'Unknown mode.' }
                }
                "Result: $result"
            }
            'OptimizeVolume' {
                $volume = Get-Target | Get-Volume
                switch ($env:PICKLE_OPTION) {
                    'retrim' { Optimize-Volume -InputObject $volume -ReTrim -Verbose 4>&1 | Out-String }
                    'defrag' { Optimize-Volume -InputObject $volume -Defrag -Verbose 4>&1 | Out-String }
                    'analyze' { Optimize-Volume -InputObject $volume -Analyze -Verbose 4>&1 | Out-String }
                    default { throw 'Unknown mode.' }
                }
            }
            'SetDiskState' {
                switch ($env:PICKLE_OPTION) {
                    'online' { Set-Disk -Number $disk -IsOffline $false }
                    'offline' { Set-Disk -Number $disk -IsOffline $true }
                    'readonly' { Set-Disk -Number $disk -IsReadOnly $true }
                    'readwrite' { Set-Disk -Number $disk -IsReadOnly $false }
                    default { throw 'Unknown state.' }
                }
            }
            'CleanDisk' { Clear-Disk -Number $disk -RemoveData -RemoveOEM -Confirm:$false }
            default { throw 'Unknown operation.' }
        }
        'OK'
        """;

    public async Task<ElevatedResponse> RunStorageOperationsAsync(IReadOnlyList<DiskOperation> operations, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var results = new List<StorageStepResult>();
        foreach (var operation in operations)
        {
            progress.Report(DiskOperationRules.Describe(operation) + "…");
            StorageStepResult step;
            try
            {
                DiskOperationRules.CheckAgainst(operation, await ReadDisksAsync(cancellationToken).ConfigureAwait(false));
                step = await RunStorageOperationAsync(operation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                step = new StorageStepResult(false, ex.Message);
            }

            results.Add(step);
            if (!step.Success)
            {
                break;
            }
        }

        var failed = results.FirstOrDefault(r => !r.Success);
        var done = results.Count(r => r.Success);
        var message = failed is null
            ? $"{done} disk change{(done == 1 ? string.Empty : "s")} applied."
            : $"{done} of {operations.Count} disk changes applied; the next one failed: {failed.Message}";
        return new ElevatedResponse(failed is null, message, failed is null ? 0 : 1, JsonSerializer.Serialize(results, PickleJson.Compact));
    }

    internal IReadOnlyDictionary<string, string?> StorageEnvironment(DiskOperation operation)
    {
        var environment = new Dictionary<string, string?>(WingetSourceRepair.Environment())
        {
            ["PICKLE_DISK_OP"] = operation.Kind.ToString(),
            ["PICKLE_DISK"] = operation.DiskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (operation.PartitionNumber is { } partition)
        {
            environment["PICKLE_PARTITION"] = partition.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (operation.SizeBytes is { } size)
        {
            environment["PICKLE_SIZE"] = size.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (operation.FileSystem is { } fileSystem)
        {
            environment["PICKLE_FS"] = DiskOperationRules.FileSystems.First(f => f.Equals(fileSystem, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(operation.Label))
        {
            environment["PICKLE_LABEL"] = operation.Label;
        }

        if (operation.DriveLetter is { } letter)
        {
            environment["PICKLE_LETTER"] = char.ToUpperInvariant(letter).ToString();
        }

        if (operation.Option is { } option)
        {
            environment["PICKLE_OPTION"] = operation.Kind == DiskOperationKind.InitializeDisk ? option.ToUpperInvariant() : option.ToLowerInvariant();
        }

        return environment;
    }

    private async Task<StorageStepResult> RunStorageOperationAsync(DiskOperation operation, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            WingetService.WindowsPowerShellPath,
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(StorageScript))],
            null,
            ElevatedOperations.TimeoutFor(ElevatedOperationKind.StorageOperations),
            cancellationToken,
            StorageEnvironment(operation)).ConfigureAwait(false);
        var text = result.Output.Trim();
        if (result.ExitCode == 0 && !result.TimedOut)
        {
            var detail = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l != "OK").ToList();
            return new StorageStepResult(true, detail.Count == 0 ? "Done." : Cut(string.Join(" ", detail), 300));
        }

        return new StorageStepResult(false, result.TimedOut ? "It timed out." : Cut(FirstLine(text, $"PowerShell exited with {result.ExitCode}."), 300));
    }

    private async Task<IReadOnlyList<PhysicalDisk>> ReadDisksAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            WingetService.WindowsPowerShellPath,
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(DiskLayoutService.Script))],
            null,
            TimeSpan.FromMinutes(1),
            cancellationToken,
            WingetSourceRepair.Environment()).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.TimedOut)
        {
            throw new InvalidOperationException("The disks could not be read: " + FirstLine(result.Output.Trim(), "PowerShell failed."));
        }

        var json = result.Output.Trim();
        var start = json.IndexOfAny(['[', '{']);
        try
        {
            return DiskLayoutService.Parse(start < 0 ? string.Empty : json[start..]);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The disk list was not understood: " + ex.Message, ex);
        }
    }
}
