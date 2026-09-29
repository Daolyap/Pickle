using System.Diagnostics;

namespace Pickle.Core.Hosting;

/// <summary>How long each startup phase took (for <c>pk doctor --startup</c>), measured from the process start.</summary>
public sealed class StartupTimings
{
    private readonly Lock _gate = new();
    private readonly List<(string Phase, TimeSpan Duration)> _phases = [];
    private long _last = Stopwatch.GetTimestamp();

    public StartupTimings()
    {
        // The .NET runtime, PowerShell's assemblies and Main up to here: from the OS's process start time.
        try
        {
            var sinceStart = DateTime.Now - Process.GetCurrentProcess().StartTime;
            if (sinceStart > TimeSpan.Zero && sinceStart < TimeSpan.FromMinutes(5))
            {
                _phases.Add(("process start", sinceStart));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public IReadOnlyList<(string Phase, TimeSpan Duration)> Phases
    {
        get
        {
            lock (_gate)
            {
                return [.. _phases];
            }
        }
    }

    public TimeSpan Total => Phases.Aggregate(TimeSpan.Zero, (sum, p) => sum + p.Duration);

    /// <summary>Ends the current phase (everything since the previous mark) under <paramref name="phase"/>.</summary>
    public void Mark(string phase)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            _phases.Add((phase, Stopwatch.GetElapsedTime(_last, now)));
            _last = now;
        }
    }

    /// <summary>"1.2 s (runspace 640 ms, plugins 180 ms, process start 150 ms)": the total and the slowest phases.</summary>
    public string Summary(int top = 3)
    {
        var phases = Phases;
        if (phases.Count == 0)
        {
            return "not measured";
        }

        var slowest = phases.OrderByDescending(p => p.Duration).Take(top).Select(p => $"{p.Phase} {Format(p.Duration)}");
        return $"{Format(Total)} ({string.Join(", ", slowest)})";
    }

    public static string Format(TimeSpan duration) =>
        duration.TotalMilliseconds >= 1000
            ? duration.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s"
            : Math.Round(duration.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms";
}
