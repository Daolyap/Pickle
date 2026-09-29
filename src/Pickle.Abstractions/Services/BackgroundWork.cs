namespace Pickle.Abstractions.Services;

/// <summary>
/// Work the first running Pickle (the primary instance) does on behalf of all of them: a slow check or refresh whose
/// result the others read from <see cref="IBackgroundWork"/>'s cache instead of repeating it.
/// </summary>
/// <param name="Id">Unique, file-name-safe id (also the cache key by convention).</param>
/// <param name="Run">The work. Runs on a background thread, one job at a time; throw or return to finish.</param>
public sealed record BackgroundJob(string Id, Func<CancellationToken, Task> Run)
{
    /// <summary>How often it repeats. Null runs it once per primary instance.</summary>
    public TimeSpan? Interval { get; init; }

    /// <summary>A run-once job is skipped when any instance ran it this recently.</summary>
    public TimeSpan MinimumGap { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Wait after startup before the first run, so the prompt comes up first.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>A value written by a background job, with when it was written.</summary>
public sealed record CachedValue<T>(DateTimeOffset At, T Value);

/// <summary>
/// Background jobs that only the first running Pickle does, and a small JSON cache every instance can read.
/// Available as a service; there is none in <c>-c</c>, script and headless runs.
/// </summary>
public interface IBackgroundWork
{
    /// <summary>True while this process is the one running the jobs (another instance may take over later).</summary>
    bool IsPrimary { get; }

    void Register(BackgroundJob job);

    /// <summary>The last value written for <paramref name="key"/> by any instance, or null.</summary>
    CachedValue<T>? Read<T>(string key);

    void Write<T>(string key, T value);
}
