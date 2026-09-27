using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Storage;

/// <summary>
/// Reads disks, partitions and volumes with the Storage module (Get-Disk, Get-Partition, Get-Volume) in a background
/// runspace. Read-only: changes go through the wizards, as commands the user sees before they run.
/// </summary>
public sealed class DiskLayoutService(Func<IPickleShell?> shell) : IDiskLayoutService
{
    internal const string Script = """
        $disks = @(Get-Disk -ErrorAction Stop | Sort-Object Number | ForEach-Object {
            $disk = $_
            $partitions = @(Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue | Sort-Object PartitionNumber | ForEach-Object {
                $volume = $_ | Get-Volume -ErrorAction SilentlyContinue
                [pscustomobject]@{
                    DiskNumber = [int]$_.DiskNumber; PartitionNumber = [int]$_.PartitionNumber
                    DriveLetter = if ($_.DriveLetter -and [int][char]$_.DriveLetter -ne 0) { [string]$_.DriveLetter } else { $null }
                    Size = [int64]$_.Size; Type = [string]$_.Type; IsBoot = [bool]$_.IsBoot; IsSystem = [bool]$_.IsSystem
                    FileSystem = if ($volume) { [string]$volume.FileSystem } else { $null }
                    Label = if ($volume) { [string]$volume.FileSystemLabel } else { $null }
                    Free = if ($volume) { [int64]$volume.SizeRemaining } else { $null }
                }
            })
            [pscustomobject]@{
                Number = [int]$disk.Number; Name = [string]$disk.FriendlyName; PartitionStyle = [string]$disk.PartitionStyle
                Size = [int64]$disk.Size; AllocatedSize = [int64]$disk.AllocatedSize; Status = [string]$disk.OperationalStatus
                IsOffline = [bool]$disk.IsOffline; IsReadOnly = [bool]$disk.IsReadOnly; IsBoot = [bool]$disk.IsBoot
                IsSystem = [bool]$disk.IsSystem; BusType = [string]$disk.BusType; Partitions = $partitions
            }
        })
        ConvertTo-Json -InputObject $disks -Depth 4 -Compress
        """;

    public async Task<IReadOnlyList<PhysicalDisk>> GetDisksAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Disk management uses the Windows Storage module.");
        }

        var target = shell() ?? throw new InvalidOperationException("The shell is not running.");
        var result = await target.InvokeAsync(Script, null, ShellTarget.Background, cancellationToken).ConfigureAwait(false);
        if (result.HadErrors && result.Output.Count == 0)
        {
            throw new InvalidOperationException(result.Errors[0].ToString());
        }

        return Parse(string.Concat(result.Output.Select(o => o?.ToString())));
    }

    /// <summary>The script's JSON → disks.</summary>
    public static IReadOnlyList<PhysicalDisk> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(json);
        var disks = new List<PhysicalDisk>();
        foreach (var d in Items(doc.RootElement))
        {
            var partitions = new List<DiskPartition>();
            if (d.TryGetProperty("Partitions", out var parts))
            {
                foreach (var p in Items(parts))
                {
                    var letter = Text(p, "DriveLetter");
                    partitions.Add(new DiskPartition(
                        Int(p, "DiskNumber"),
                        Int(p, "PartitionNumber"),
                        letter is { Length: 1 } l && char.IsAsciiLetter(l[0]) ? char.ToUpperInvariant(l[0]) : null,
                        Long(p, "Size") ?? 0,
                        Text(p, "Type") ?? string.Empty,
                        Bool(p, "IsBoot"),
                        Bool(p, "IsSystem"),
                        Text(p, "FileSystem") is { Length: > 0 } fs ? fs : null,
                        Text(p, "Label") is { Length: > 0 } label ? label : null,
                        Long(p, "Free")));
                }
            }

            disks.Add(new PhysicalDisk(
                Int(d, "Number"),
                Text(d, "Name") ?? "Disk",
                Text(d, "PartitionStyle") ?? string.Empty,
                Long(d, "Size") ?? 0,
                Long(d, "AllocatedSize") ?? 0,
                Text(d, "Status") ?? string.Empty,
                Bool(d, "IsOffline"),
                Bool(d, "IsReadOnly"),
                Bool(d, "IsBoot"),
                Bool(d, "IsSystem"),
                Text(d, "BusType") ?? string.Empty,
                partitions));
        }

        return disks;
    }

    // ConvertTo-Json writes a one-element array as a bare object on Windows PowerShell; accept both.
    private static IEnumerable<JsonElement> Items(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Array => element.EnumerateArray(),
            JsonValueKind.Object => [element],
            _ => [],
        };

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static int Int(JsonElement e, string name) => (int)(Long(e, name) ?? 0);

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
