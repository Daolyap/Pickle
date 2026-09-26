using System.Globalization;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui.Panels.SystemMonitoring;

namespace Pickle.Tui.Panels.Dashboard;

/// <summary>Everything one dashboard frame shows. Histories are oldest first.</summary>
internal sealed record DashboardData(
    SystemSummary? System,
    ProcessSnapshot? Processes,
    IReadOnlyList<NetworkInterfaceSample> Interfaces,
    IReadOnlyList<DiskVolume> Volumes,
    IReadOnlyList<DiskIoSample> DiskIo,
    IReadOnlyList<double> CpuHistory,
    IReadOnlyList<double> MemoryHistory,
    IReadOnlyList<double> DownHistory,
    IReadOnlyList<double> UpHistory,
    bool Windows);

/// <summary>
/// Lays the dashboard out as tiles: System, CPU and memory, Network, Disks, and the busiest processes by CPU and by
/// memory. Two columns from 100 columns wide, one below that; the process lists give way first when short of rows.
/// </summary>
internal static class DashboardLayout
{
    private const int TwoColumns = 100;

    /// <summary>Rows the layout wants at this width (the panel gets fewer when the terminal is short).</summary>
    public static int NaturalHeight(DashboardData data, int width) => Plan(data, width, int.MaxValue).Sum(r => r.Height);

    public static DashboardCanvas Render(DashboardData data, int width, int height)
    {
        var canvas = new DashboardCanvas(width, height);
        var y = 0;
        foreach (var row in Plan(data, width, height))
        {
            var x = 0;
            for (var i = 0; i < row.Tiles.Count; i++)
            {
                var tileWidth = i == row.Tiles.Count - 1 ? width - x : width / row.Tiles.Count;
                row.Tiles[i](canvas, new Tile(x, y, tileWidth, row.Height), data);
                x += tileWidth;
            }

            y += row.Height;
        }

        return canvas;
    }

    private sealed record Row(int Height, IReadOnlyList<Action<DashboardCanvas, Tile, DashboardData>> Tiles);

    private readonly record struct Tile(int X, int Y, int Width, int Height)
    {
        public int Left => X + 2;

        public int Right => X + Width - 2;

        public int Inner => Math.Max(0, Width - 4);

        public int Line(int n) => Y + 1 + n;

        public int Lines => Math.Max(0, Height - 2);
    }

    private static List<Row> Plan(DashboardData data, int width, int height)
    {
        var networkLines = 3 + Math.Min(4, ActiveInterfaces(data).Count);
        var diskLines = Math.Max(1, Math.Min(8, data.Volumes.Count)) + (data.DiskIo.Count > 0 ? 1 : 0);
        const int SystemLines = 7;
        const int CpuMemoryLines = 7;
        var topLines = Math.Clamp(data.Processes?.Processes.Count ?? 0, 3, 10);

        if (width >= TwoColumns)
        {
            var first = Math.Max(SystemLines, CpuMemoryLines) + 2;
            var second = Math.Max(networkLines, diskLines) + 2;
            var third = Math.Min(topLines + 2, Math.Max(0, height - first - second));
            var rows = new List<Row>
            {
                new(first, [SystemTile, CpuMemoryTile]),
                new(second, [DisksTile, NetworkTile]),
            };
            if (third >= 4)
            {
                rows.Add(new Row(third, [TopCpuTile, TopMemoryTile]));
            }

            return rows;
        }

        var stack = new List<Row>
        {
            new(SystemLines + 2, [SystemTile]),
            new(CpuMemoryLines + 2, [CpuMemoryTile]),
            new(networkLines + 2, [NetworkTile]),
            new(diskLines + 2, [DisksTile]),
            new(topLines + 2, [TopCpuTile]),
        };
        var used = 0;
        var fitting = new List<Row>();
        foreach (var row in stack)
        {
            if (used + row.Height > height)
            {
                break;
            }

            fitting.Add(row);
            used += row.Height;
        }

        return fitting;
    }

    private static void SystemTile(DashboardCanvas c, Tile t, DashboardData s)
    {
        c.Box(t.X, t.Y, t.Width, t.Height, "System");
        if (s.System is not { } sys)
        {
            c.Text(t.Left, t.Line(0), "reading…", CellRole.Muted);
            return;
        }

        var lines = new List<(string Label, string Value, CellRole Role)>
        {
            ("host", $"{sys.MachineName} ({sys.UserName})", CellRole.Accent),
            ("os", sys.OperatingSystem, CellRole.Normal),
            ("cpu", (sys.CpuModel is { } model ? model + " · " : string.Empty) + $"{sys.LogicalProcessors} threads", CellRole.Normal),
            ("uptime", Uptime(sys.Uptime), CellRole.Normal),
        };
        if (sys.Battery is { } battery)
        {
            var charge = battery.Percent is { } p ? p.ToString(CultureInfo.InvariantCulture) + "%" : "battery";
            var state = battery.Charging == true ? "charging" : battery.OnAcPower ? "plugged in" : "on battery";
            lines.Add(("power", $"{charge} · {state}", battery.Percent is < 20 && battery.Charging != true ? CellRole.Error : CellRole.Normal));
        }

        lines.Add(sys.RestartPendingReason is { } reason
            ? ("restart", "needed for " + reason, CellRole.Warning)
            : ("restart", "not needed", CellRole.Success));
        var processes = s.Processes is { } snapshot
            ? $"{SystemFormat.Count(snapshot.Processes.Count)} processes · {SystemFormat.Count(snapshot.Processes.Sum(p => (long)p.Threads))} threads"
            : "…";
        lines.Add(("running", processes, CellRole.Normal));

        for (var i = 0; i < Math.Min(lines.Count, t.Lines); i++)
        {
            var (label, value, role) = lines[i];
            c.Text(t.Left, t.Line(i), label, CellRole.Muted);
            c.Text(t.Left + 9, t.Line(i), value, role, t.Inner - 9);
        }
    }

    private static void CpuMemoryTile(DashboardCanvas c, Tile t, DashboardData s)
    {
        c.Box(t.X, t.Y, t.Width, t.Height, "CPU & memory");
        var cpu = s.Processes?.CpuPercent ?? 0;
        var line = 0;
        Meter(c, t, line++, "CPU", SystemFormat.Percent(cpu), cpu / 100);
        Spark(c, t, line++, s.CpuHistory, 100, cpu / 100);
        if (s.System?.LoadAverage is { Count: 3 } load)
        {
            c.Text(t.Left, t.Line(line), "load", CellRole.Muted);
            c.Text(t.Left + 9, t.Line(line++), string.Join("  ", load.Select(l => l.ToString("0.00", CultureInfo.InvariantCulture))) + "  (1, 5, 15 min)");
        }

        if (s.Processes is { MemoryTotal: > 0 } p)
        {
            var fraction = (double)p.MemoryUsed / p.MemoryTotal;
            Meter(c, t, line++, "memory", $"{SystemFormat.Bytes(p.MemoryUsed)} / {SystemFormat.Bytes(p.MemoryTotal)}", fraction);
            Spark(c, t, line++, s.MemoryHistory, 100, fraction);
        }

        if (s.System is { SwapTotal: > 0 } sys && line < t.Lines)
        {
            var fraction = (double)(sys.SwapUsed ?? 0) / sys.SwapTotal!.Value;
            Meter(c, t, line, s.Windows ? "commit" : "swap", $"{SystemFormat.Bytes(sys.SwapUsed ?? 0)} / {SystemFormat.Bytes(sys.SwapTotal.Value)}", fraction);
        }
    }

    private static void NetworkTile(DashboardCanvas c, Tile t, DashboardData s)
    {
        c.Box(t.X, t.Y, t.Width, t.Height, "Network");
        var down = s.Interfaces.Sum(i => i.ReceiveRate);
        var up = s.Interfaces.Sum(i => i.SendRate);
        c.Text(t.Left, t.Line(0), "↓ ", CellRole.Muted);
        var x = t.Left + 2 + c.Text(t.Left + 2, t.Line(0), SystemFormat.Rate(down), CellRole.Accent);
        c.Text(x + 3, t.Line(0), "↑ ", CellRole.Muted);
        c.Text(x + 5, t.Line(0), SystemFormat.Rate(up), CellRole.Info);
        c.Text(t.Left, t.Line(1), SystemFormat.Sparkline(s.DownHistory, t.Inner), CellRole.Accent);
        c.Text(t.Left, t.Line(2), SystemFormat.Sparkline(s.UpHistory, t.Inner), CellRole.Info);
        var line = 3;
        foreach (var nic in ActiveInterfaces(s).Take(Math.Max(0, t.Lines - 3)))
        {
            var address = nic.IPv4.FirstOrDefault() ?? nic.IPv6.FirstOrDefault() ?? string.Empty;
            var nameWidth = Math.Min(16, Math.Max(8, t.Inner / 3));
            c.Text(t.Left, t.Line(line), nic.Name, CellRole.Normal, nameWidth);
            c.Text(t.Left + nameWidth + 1, t.Line(line), address, CellRole.Muted, Math.Max(0, t.Inner - nameWidth - 1));
            var rates = $"↓{SystemFormat.Rate(nic.ReceiveRate)} ↑{SystemFormat.Rate(nic.SendRate)}";
            if (nameWidth + 1 + address.Length + 2 + rates.Length <= t.Inner)
            {
                c.TextRight(t.Right, t.Line(line), rates, CellRole.Muted);
            }

            line++;
        }
    }

    private static void DisksTile(DashboardCanvas c, Tile t, DashboardData s)
    {
        c.Box(t.X, t.Y, t.Width, t.Height, "Disks");
        if (s.Volumes.Count == 0)
        {
            c.Text(t.Left, t.Line(0), "reading…", CellRole.Muted);
            return;
        }

        var line = 0;
        var nameWidth = Math.Min(12, s.Volumes.Max(v => v.Name.Length));
        foreach (var volume in s.Volumes.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Take(Math.Max(0, t.Lines - (s.DiskIo.Count > 0 ? 1 : 0))))
        {
            var free = $"{SystemFormat.Bytes(volume.FreeBytes)} free";
            var percent = (volume.UsedFraction * 100).ToString("0", CultureInfo.InvariantCulture).PadLeft(3) + "%";
            var barWidth = Math.Max(4, t.Inner - nameWidth - free.Length - percent.Length - 4);
            c.Text(t.Left, t.Line(line), volume.Name, CellRole.Normal, nameWidth);
            c.Text(t.Left + nameWidth + 1, t.Line(line), SystemFormat.Bar(volume.UsedFraction, barWidth), Level(volume.UsedFraction));
            c.Text(t.Left + nameWidth + 2 + barWidth, t.Line(line), percent, Level(volume.UsedFraction));
            c.TextRight(t.Right, t.Line(line), free, CellRole.Muted);
            line++;
        }

        if (s.DiskIo.Count > 0 && line < t.Lines)
        {
            c.Text(t.Left, t.Line(line), $"read {SystemFormat.Rate(s.DiskIo.Sum(d => d.ReadRate))} · write {SystemFormat.Rate(s.DiskIo.Sum(d => d.WriteRate))}", CellRole.Muted);
        }
    }

    private static void TopCpuTile(DashboardCanvas c, Tile t, DashboardData s) =>
        TopProcesses(c, t, s, "Busiest (CPU)", p => p.CpuPercent, p => SystemFormat.Percent(p.CpuPercent));

    private static void TopMemoryTile(DashboardCanvas c, Tile t, DashboardData s) =>
        TopProcesses(c, t, s, "Largest (memory)", p => p.WorkingSet, p => SystemFormat.Bytes(p.WorkingSet));

    private static void TopProcesses(DashboardCanvas c, Tile t, DashboardData s, string title, Func<ProcessSample, double> key, Func<ProcessSample, string> value)
    {
        c.Box(t.X, t.Y, t.Width, t.Height, title);
        var processes = s.Processes?.Processes ?? [];
        var line = 0;
        foreach (var p in processes.OrderByDescending(key).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(t.Lines))
        {
            var shown = value(p);
            var id = p.Id.ToString(CultureInfo.InvariantCulture);
            c.Text(t.Left, t.Line(line), p.Name, CellRole.Normal, Math.Max(0, t.Inner - shown.Length - id.Length - 3));
            c.TextRight(t.Right - shown.Length - 2, t.Line(line), id, CellRole.Muted);
            c.TextRight(t.Right, t.Line(line), shown, line == 0 ? CellRole.Accent : CellRole.Normal);
            line++;
        }
    }

    private static void Meter(DashboardCanvas c, Tile t, int line, string label, string value, double fraction)
    {
        c.Text(t.Left, t.Line(line), label, CellRole.Muted);
        var valueWidth = c.Text(t.Left + 9, t.Line(line), value, CellRole.Accent);
        var barX = t.Left + 9 + valueWidth + 2;
        var barWidth = t.Right - barX;
        if (barWidth >= 4)
        {
            c.Text(barX, t.Line(line), SystemFormat.Bar(fraction, barWidth), Level(fraction));
        }
    }

    private static void Spark(DashboardCanvas c, Tile t, int line, IReadOnlyList<double> history, double max, double level) =>
        c.Text(t.Left + 9, t.Line(line), SystemFormat.Sparkline(history, Math.Max(0, t.Inner - 9), max), Level(level));

    private static CellRole Level(double fraction) => fraction >= 0.9 ? CellRole.Error : fraction >= 0.7 ? CellRole.Warning : CellRole.Success;

    private static List<NetworkInterfaceSample> ActiveInterfaces(DashboardData data) =>
    [
        .. data.Interfaces
            .Where(i => i.Status.Equals("Up", StringComparison.OrdinalIgnoreCase) && i.Type != "Loopback" && (i.IPv4.Count > 0 || i.IPv6.Count > 0))
            .OrderByDescending(i => i.ReceiveRate + i.SendRate)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>"3d 4h 12m", "4h 12m", "12m".</summary>
    public static string Uptime(TimeSpan uptime) =>
        uptime.TotalDays >= 1 ? $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m"
        : uptime.TotalHours >= 1 ? $"{uptime.Hours}h {uptime.Minutes}m"
        : $"{Math.Max(0, uptime.Minutes)}m";
}
