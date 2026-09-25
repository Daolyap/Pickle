using Pickle.Abstractions.Services;

namespace Pickle.Windows.WindowsUpdate;

/// <summary>
/// One running Windows Update search per criteria. The agent's synchronous search can't be aborted, so a caller that
/// gives up (Ctrl+C, timeout, panel closed) only stops waiting; the next caller with the same criteria joins that
/// search instead of starting a second one, which the agent would queue behind the first.
/// </summary>
internal sealed class SharedSearch<T>
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Running> _running = new(StringComparer.Ordinal);

    public int RunningCount
    {
        get
        {
            lock (_gate)
            {
                return _running.Count;
            }
        }
    }

    /// <param name="start">Starts the search; its argument forwards progress to every current waiter (any thread).</param>
    public async Task<T> RunAsync(
        string key,
        Func<Action<WindowsUpdateProgress>, Task<T>> start,
        IProgress<WindowsUpdateProgress>? progress,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Running running;
        bool joined;
        lock (_gate)
        {
            joined = _running.TryGetValue(key, out running!);
            if (!joined)
            {
                running = new Running();
                _running[key] = running;
            }
        }

        running.Subscribe(progress);
        if (joined)
        {
            progress?.Report(new WindowsUpdateProgress("Waiting", "for the search already in progress", null));
        }
        else
        {
            Task<T> task;
            try
            {
                task = start(running.Report);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                task = Task.FromException<T>(ex);
            }

            running.Task.SetResult(task);
            _ = task.ContinueWith(
                _ =>
                {
                    lock (_gate)
                    {
                        if (_running.TryGetValue(key, out var current) && ReferenceEquals(current, running))
                        {
                            _running.Remove(key);
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                System.Threading.Tasks.TaskScheduler.Default);
        }

        try
        {
            var search = await running.Task.Task.ConfigureAwait(false);
            return await search.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            running.Unsubscribe(progress);
        }
    }

    private sealed class Running
    {
        private readonly List<IProgress<WindowsUpdateProgress>> _subscribers = [];

        public TaskCompletionSource<Task<T>> Task { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Subscribe(IProgress<WindowsUpdateProgress>? progress)
        {
            if (progress is not null)
            {
                lock (_subscribers)
                {
                    _subscribers.Add(progress);
                }
            }
        }

        public void Unsubscribe(IProgress<WindowsUpdateProgress>? progress)
        {
            if (progress is not null)
            {
                lock (_subscribers)
                {
                    _subscribers.Remove(progress);
                }
            }
        }

        public void Report(WindowsUpdateProgress value)
        {
            IProgress<WindowsUpdateProgress>[] targets;
            lock (_subscribers)
            {
                targets = [.. _subscribers];
            }

            foreach (var target in targets)
            {
                target.Report(value);
            }
        }
    }
}
