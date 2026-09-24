using System.Globalization;
using System.Text;

namespace Pickle.Tui.Panels.SystemMonitoring;

/// <summary>Number formatting shared by the system panels (binary units, rates, percentages, bars, sparklines).</summary>
internal static class SystemFormat
{
    private const string Blocks = "▁▂▃▄▅▆▇█";
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long bytes) => Bytes((double)bytes);

    public static string Bytes(double bytes)
    {
        var value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 ? "0" : value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + ByteUnits[unit];
    }

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Bits(long? bitsPerSecond) => bitsPerSecond switch
    {
        null or <= 0 => string.Empty,
        >= 1_000_000_000 => (bitsPerSecond.Value / 1e9).ToString("0.#", CultureInfo.InvariantCulture) + " Gb/s",
        >= 1_000_000 => (bitsPerSecond.Value / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + " Mb/s",
        _ => (bitsPerSecond.Value / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + " Kb/s",
    };

    public static string Percent(double percent) => percent.ToString("0.0", CultureInfo.InvariantCulture) + "%";

    public static string Count(long count) => count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"██████░░░░" for a 0–1 fraction.</summary>
    public static string Bar(double fraction, int width)
    {
        var filled = (int)Math.Round(Math.Clamp(fraction, 0, 1) * width);
        return new string('█', filled) + new string('░', width - filled);
    }

    /// <summary>The last <paramref name="width"/> values as block characters, scaled to <paramref name="max"/> (default: the largest value).</summary>
    public static string Sparkline(IReadOnlyList<double> values, int width, double? max = null)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var start = Math.Max(0, values.Count - width);
        var top = max ?? 0;
        if (max is null)
        {
            for (var i = start; i < values.Count; i++)
            {
                top = Math.Max(top, values[i]);
            }
        }

        var sb = new StringBuilder(width);
        sb.Append(' ', width - (values.Count - start));
        for (var i = start; i < values.Count; i++)
        {
            var level = top > 0 ? Math.Clamp(values[i] / top, 0, 1) : 0;
            sb.Append(Blocks[(int)Math.Round(level * (Blocks.Length - 1))]);
        }

        return sb.ToString();
    }

    public static string Time(DateTimeOffset? time, DateTimeOffset now)
    {
        if (time is not { } t)
        {
            return string.Empty;
        }

        var local = t.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static string Duration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}.{duration.Milliseconds / 10:00}";
}

/// <summary>The most recent values of a series (oldest first), for sparklines.</summary>
internal sealed class SampleHistory(int capacity = 120)
{
    private readonly List<double> _values = [];

    public IReadOnlyList<double> Values => _values;

    public double Last => _values.Count > 0 ? _values[^1] : 0;

    public void Add(double value)
    {
        if (_values.Count >= capacity)
        {
            _values.RemoveAt(0);
        }

        _values.Add(value);
    }
}
