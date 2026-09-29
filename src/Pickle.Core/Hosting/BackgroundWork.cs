using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.Contracts;
using Pickle.Core.Terminal;

namespace Pickle.Core.Hosting;

/// <summary>
/// Runs <see cref="BackgroundJob"/>s in the first interactive Pickle only. That instance holds an exclusive lock on
/// DataDir/primary.lock, which the OS releases when it exits (even if it crashes); the others retry every minute, so
/// one of them takes over. When each job last ran (by any instance) and the jobs' cached results live in
/// CacheDir/background, so a new primary doesn't repeat work another instance did minutes ago.
/// </summary>
internal sealed class BackgroundWork : IBackgroundWork, IRuntimeComponent, IDisposable
{
    private const string StateKey = "_state";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly PickleRuntime _runtime;
    private readonly Lock _gate = new();
    private readonly List<BackgroundJob> _jobs = [];
    private readonly HashSet<string> _settled = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private FileStream? _primaryLock;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _nextPrimaryAttempt;
    private Task? _loop;

    public BackgroundWork(PickleRuntime runtime) => _runtime = runtime;

    public bool IsPrimary => _primaryLock is not null;

    internal TimeSpan Tick { get; set; } = TimeSpan.FromSeconds(15);

    internal TimeSpan PrimaryRetry { get; set; } = TimeSpan.FromMinutes(1);

    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    internal string LockFile => Path.Combine(_runtime.Paths.DataDir, "primary.lock");

    private string CacheDirectory => Path.Combine(_runtime.Paths.CacheDir, "background");

    public void Initialize()
    {
    }

    public void OnStarted()
    {
        // Only a real interactive shell: not -c, scripts, headless runs or test runtimes.
        if (_runtime.Terminal is ConsoleTerminal { IsInteractive: true } && !_runtime.Options.Headless
            && _runtime.Options.Command is null && _runtime.Options.File is null)
        {
            Start();
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }

            _startedAt = Clock();
            var token = _stop.Token;
            _loop = Task.Run(() => LoopAsync(token), token);
        }
    }

    public void Register(BackgroundJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrWhiteSpace(job.Id) || job.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || job.Id == StateKey)
        {
            throw new ArgumentException($"'{job.Id}' is not a valid background job id.", nameof(job));
        }

        lock (_gate)
        {
            _jobs.RemoveAll(j => j.Id == job.Id);
            _jobs.Add(job);
        }
    }

    public CachedValue<T>? Read<T>(string key)
    {
        try
        {
            var path = CachePath(key);
            return File.Exists(path) ? JsonSerializer.Deserialize<CachedValue<T>>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _runtime.Log.Debug("background", $"cache '{key}' unreadable: {ex.Message}");
            return null;
        }
    }

    public void Write<T>(string key, T value)
    {
        var path = CachePath(key);
        Directory.CreateDirectory(CacheDirectory);

        // Write-then-rename so a reader in another instance never sees half a file.
        var temp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new CachedValue<T>(Clock(), value), Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Takes the primary lock if no other instance holds it.</summary>
    internal bool TryBecomePrimary()
    {
        if (IsPrimary)
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(_runtime.Paths.DataDir);
            var stream = new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            var pid = System.Text.Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            stream.Write(pid);
            stream.Flush();
            _primaryLock = stream;
            _runtime.Log.Info("background", "This instance runs the background jobs.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>One scheduler pass: become primary if possible, then run whatever is due.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!IsPrimary && Clock() >= _nextPrimaryAttempt)
        {
            _nextPrimaryAttempt = Clock() + PrimaryRetry;
            TryBecomePrimary();
        }

        if (!IsPrimary)
        {
            return;
        }

        List<BackgroundJob> jobs;
        lock (_gate)
        {
            jobs = [.. _jobs];
        }

        var state = Read<Dictionary<string, DateTimeOffset>>(StateKey)?.Value ?? [];
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsDue(job, state.TryGetValue(job.Id, out var last) ? last : null))
            {
                continue;
            }

            try
            {
                await job.Run(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _runtime.Log.Warn("background", $"Background job '{job.Id}' failed: {ex.Message}", ex);
            }

            // Recorded even after a failure: a broken job waits its interval rather than retrying every tick.
            state[job.Id] = Clock();
            try
            {
                Write(StateKey, state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _runtime.Log.Debug("background", $"state not saved: {ex.Message}");
            }
        }
    }

    private bool IsDue(BackgroundJob job, DateTimeOffset? last)
    {
        var now = Clock();
        if (now - _startedAt < job.InitialDelay)
        {
            return false;
        }

        if (job.Interval is { } every)
        {
            return last is null || now - last >= every;
        }

        // Run-once: decided the first time it could run; skipped for good when another instance just did it.
        if (!_settled.Add(job.Id))
        {
            return false;
        }

        return last is null || now - last >= job.MinimumGap;
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            do
            {
                await RunOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private string CachePath(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{key}' is not a valid cache key.", nameof(key));
        }

        return Path.Combine(CacheDirectory, key + ".json");
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _primaryLock?.Dispose();
        _primaryLock = null;
        _stop.Dispose();
    }
}
