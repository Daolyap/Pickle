namespace Pickle.Core.SystemMonitoring;

/// <summary>Turns cumulative counters (bytes sent, sectors read, ...) into per-second rates between successive samples.</summary>
public sealed class RateTracker
{
    private readonly Dictionary<string, (long Value, TimeSpan Time)> _last = new(StringComparer.Ordinal);

    /// <summary>
    /// The rate of <paramref name="key"/> since its previous update. 0 the first time, when no time passed, or when the
    /// counter went backwards (interface reset, counter wrap).
    /// </summary>
    public double Update(string key, long value, TimeSpan time)
    {
        var rate = 0.0;
        if (_last.TryGetValue(key, out var previous))
        {
            var seconds = (time - previous.Time).TotalSeconds;
            var delta = value - previous.Value;
            if (seconds > 0 && delta > 0)
            {
                rate = delta / seconds;
            }
        }

        _last[key] = (value, time);
        return rate;
    }

    /// <summary>Forget counters that are not in <paramref name="keys"/> (devices or interfaces that went away).</summary>
    public void Retain(IReadOnlySet<string> keys)
    {
        foreach (var key in _last.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            _last.Remove(key);
        }
    }
}
