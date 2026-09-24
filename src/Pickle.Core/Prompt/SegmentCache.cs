using System.Collections.Concurrent;
using System.Diagnostics;
using Pickle.Abstractions;

namespace Pickle.Core.Prompt;

/// <summary>
/// Results of segments that complete asynchronously (git, node), keyed by segment + directory + options.
/// A value is fresh until <see cref="Invalidate"/> (called after every command). Rendering never waits past its
/// deadline: a cold cache shows nothing, a stale value is shown when the refresh is late, and keys whose last
/// refresh was slower than the timeout show their stale value immediately instead of waiting at all.
/// Synchronous segments bypass the cache entirely.
/// </summary>
internal sealed class SegmentCache
{
    private const int MaxEntries = 512;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly IPickleLogger? _log;
    private long _generation;

    public SegmentCache(IPickleLogger? log = null) => _log = log;

    /// <summary>Raised (on a thread-pool thread) when a refresh that a render gave up on has finished.</summary>
    public event EventHandler? LateResultAvailable;

    public long Generation => Interlocked.Read(ref _generation);

    public int Count => _entries.Count;

    public void Invalidate() => Interlocked.Increment(ref _generation);

    /// <summary>Starts (or reuses) a refresh. Never blocks on asynchronous work.</summary>
    public Slot Begin(string key, Func<ValueTask<PromptSegmentOutput?>> render)
    {
        var generation = Generation;
        if (_entries.TryGetValue(key, out var existing))
        {
            lock (existing)
            {
                existing.LastUsed = Stopwatch.GetTimestamp();
                if (existing.HasValue && existing.ValueGeneration == generation)
                {
                    return Slot.Ready(existing.Value);
                }

                if (existing.Pending is { } pending && existing.PendingGeneration == generation)
                {
                    // Completed but its continuation has not stored the value yet.
                    if (pending.IsCompleted)
                    {
                        return Slot.Ready(pending.IsCompletedSuccessfully ? pending.Result : null);
                    }

                    return new Slot(null, false, existing, pending);
                }
            }
        }

        ValueTask<PromptSegmentOutput?> valueTask;
        try
        {
            valueTask = render();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.Debug("prompt", $"segment {key.Split('\u0001')[0]} failed: {ex.Message}");
            return Slot.Ready(null);
        }

        if (valueTask.IsCompletedSuccessfully)
        {
            var value = valueTask.Result;
            if (existing is not null)
            {
                Store(existing, generation, value, 0);
            }

            return Slot.Ready(value);
        }

        var task = valueTask.AsTask();
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        var started = Stopwatch.GetTimestamp();
        lock (entry)
        {
            entry.LastUsed = started;
            entry.Pending = task;
            entry.PendingGeneration = generation;
        }

        task.ContinueWith(
            t =>
            {
                if (t.IsFaulted)
                {
                    _log?.Debug("prompt", $"segment {key.Split('\u0001')[0]} failed: {t.Exception?.GetBaseException().Message}");
                }

                var notify = Store(entry, generation, t.IsCompletedSuccessfully ? t.Result : null, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                lock (entry)
                {
                    if (ReferenceEquals(entry.Pending, t))
                    {
                        entry.Pending = null;
                    }
                }

                if (notify)
                {
                    LateResultAvailable?.Invoke(this, EventArgs.Empty);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Trim();
        return new Slot(null, false, entry, task);
    }

    /// <summary>The slot's value, waiting at most until <paramref name="deadline"/> (a Stopwatch timestamp).</summary>
    public PromptSegmentOutput? Collect(Slot slot, long deadline, double timeoutMs)
    {
        if (slot.IsReady || slot.Entry is null || slot.Task is null)
        {
            return slot.Value;
        }

        bool hasStale;
        PromptSegmentOutput? stale;
        lock (slot.Entry)
        {
            hasStale = slot.Entry.HasValue;
            stale = slot.Entry.Value;
            slot.Entry.TimeoutMs = timeoutMs;
            if (hasStale && slot.Entry.KnownSlow)
            {
                slot.Entry.RenderGaveUp = true;
                return stale;
            }
        }

        var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
        if (remaining > TimeSpan.Zero)
        {
            try
            {
                slot.Task.Wait(remaining);
            }
            catch (AggregateException)
            {
            }
        }

        if (slot.Task.IsCompleted)
        {
            return slot.Task.IsCompletedSuccessfully ? slot.Task.Result : null;
        }

        lock (slot.Entry)
        {
            slot.Entry.RenderGaveUp = true;
            slot.Entry.WaitTimedOut = true;
        }

        return hasStale ? stale : null;
    }

    private static bool Store(Entry entry, long generation, PromptSegmentOutput? value, double durationMs)
    {
        lock (entry)
        {
            if (generation >= entry.ValueGeneration)
            {
                entry.Value = value;
                entry.HasValue = true;
                entry.ValueGeneration = generation;
            }

            // Slow if a render actually timed out on this refresh; a refresh that finished within the timeout while
            // the prompt showed the stale value clears the flag, so the next render waits for fresh data again.
            entry.KnownSlow = entry.WaitTimedOut || durationMs >= entry.TimeoutMs;
            entry.WaitTimedOut = false;
            var notify = entry.RenderGaveUp;
            entry.RenderGaveUp = false;
            return notify;
        }
    }

    private void Trim()
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        foreach (var key in _entries.OrderBy(e => e.Value.LastUsed).Take(_entries.Count - (MaxEntries * 3 / 4)).Select(e => e.Key).ToList())
        {
            _entries.TryRemove(key, out _);
        }
    }

    internal readonly record struct Slot(PromptSegmentOutput? Value, bool IsReady, Entry? Entry, Task<PromptSegmentOutput?>? Task)
    {
        public static Slot Ready(PromptSegmentOutput? value) => new(value, true, null, null);
    }

    internal sealed class Entry
    {
        public PromptSegmentOutput? Value;
        public bool HasValue;
        public long ValueGeneration = -1;
        public Task<PromptSegmentOutput?>? Pending;
        public long PendingGeneration = -1;
        public double TimeoutMs = double.MaxValue;
        public bool KnownSlow;
        public bool WaitTimedOut;
        public bool RenderGaveUp;
        public long LastUsed;
    }
}
