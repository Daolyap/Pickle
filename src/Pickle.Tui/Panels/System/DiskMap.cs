using Pickle.Abstractions.Services;
using Pickle.Tui.Widgets;

namespace Pickle.Tui.Panels.SystemMonitoring;

/// <summary>
/// The Disk Management-style strip for every disk: a header, a bar with one block run per partition and the free space
/// (█ data, ▒ Windows' own partitions, ▚ planned, ░ unallocated) and the drive letters and sizes beneath.
/// </summary>
internal static class DiskMap
{
    private const int MinBarWidth = 24;

    public static IReadOnlyList<PreviewLine> Render(IReadOnlyList<PhysicalDisk> disks, int width)
    {
        var lines = new List<PreviewLine>();
        var barWidth = Math.Max(MinBarWidth, width - 4);
        foreach (var disk in disks.OrderBy(d => d.Number))
        {
            var state = new[] { disk.IsRaw ? "not initialized" : disk.PartitionStyle, disk.IsOffline ? "offline" : null, disk.IsReadOnly ? "read-only" : null, disk.BusType }
                .Where(s => !string.IsNullOrEmpty(s));
            lines.Add(new PreviewLine($"Disk {disk.Number}  {disk.Name}  ·  {DiskOperationRules.FormatBytes(disk.Size)}  ·  {string.Join(" · ", state)}"));
            var segments = Segments(disk);
            var widths = Widths(segments.Select(s => s.Size).ToList(), barWidth);
            lines.Add(new PreviewLine(" ▕" + string.Concat(segments.Select((s, i) => new string(s.Glyph, widths[i]))) + "▏", Muted: disk.IsOffline));
            lines.Add(new PreviewLine("  " + string.Concat(segments.Select((s, i) => Fit(s.Label, widths[i]))), Muted: true));
        }

        return lines;
    }

    private sealed record Segment(long Size, char Glyph, string Label);

    private static List<Segment> Segments(PhysicalDisk disk)
    {
        if (disk.IsRaw)
        {
            return [new Segment(Math.Max(1, disk.Size), '░', "not initialized")];
        }

        var segments = disk.Partitions
            .OrderBy(p => p.PartitionNumber)
            .Select(p => new Segment(
                Math.Max(1, p.Size),
                DiskOperationRules.IsPlanned(p) ? '▚' : p.Type is "Basic" or "IFS" ? '█' : '▒',
                string.Join(' ', new[] { DiskOperationRules.IsPlanned(p) ? "new" : null, p.DriveLetter is { } l ? l + ":" : null, DiskOperationRules.FormatBytes(p.Size) }.Where(s => s is not null))))
            .ToList();
        if (disk.UnallocatedBytes > 0)
        {
            segments.Add(new Segment(disk.UnallocatedBytes, '░', "free " + DiskOperationRules.FormatBytes(disk.UnallocatedBytes)));
        }

        return segments.Count == 0 ? [new Segment(Math.Max(1, disk.Size), '░', "empty")] : segments;
    }

    /// <summary>Proportional widths that add up to <paramref name="total"/>, at least one cell each; the largest segment absorbs the rounding.</summary>
    internal static int[] Widths(IReadOnlyList<long> sizes, int total)
    {
        var sum = (double)sizes.Sum();
        var widths = sizes.Select(s => Math.Max(1, (int)Math.Round(s / sum * total))).ToArray();
        var difference = total - widths.Sum();
        var biggest = Array.IndexOf(widths, widths.Max());
        widths[biggest] = Math.Max(1, widths[biggest] + difference);
        return widths;
    }

    private static string Fit(string text, int width) =>
        width <= 2 ? new string(' ', width) : text.Length <= width ? text.PadRight(width) : text[..(width - 1)] + "…";
}
