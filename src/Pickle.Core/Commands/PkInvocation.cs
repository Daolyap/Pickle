using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Pickle.Abstractions;

namespace Pickle.Core.Commands;

/// <summary>
/// The `pk` pipeline currently running. While a `pk` command runs, its cmdlet holds the main runspace, so anything
/// that needs the runspace (or the cmdlet's output streams) from another thread is posted here and executed by the
/// cmdlet's pump on the pipeline thread. Flows across awaits (AsyncLocal).
/// </summary>
public sealed class PkInvocation
{
    private static readonly AsyncLocal<PkInvocation?> CurrentLocal = new();
    private readonly BlockingCollection<(Action Run, Action<Exception>? Abandon)> _queue = new();
    private readonly object _gate = new();
    private bool _completed;

    public PkInvocation()
    {
        PipelineThreadId = Environment.CurrentManagedThreadId;
        Runspace = Runspace.DefaultRunspace;
    }

    public static PkInvocation? Current => CurrentLocal.Value;

    public int PipelineThreadId { get; }

    /// <summary>The runspace whose pipeline runs the command (the main one, or a background pool runspace).</summary>
    public Runspace? Runspace { get; }

    public bool IsCompleted
    {
        get
        {
            lock (_gate)
            {
                return _completed;
            }
        }
    }

    public bool IsPipelineThread => Environment.CurrentManagedThreadId == PipelineThreadId;

    public static IDisposable Enter(PkInvocation invocation)
    {
        var previous = CurrentLocal.Value;
        CurrentLocal.Value = invocation;
        return new Restore(previous);
    }

    /// <summary>Queue work for the pipeline thread. False once the command has finished.</summary>
    public bool TryPost(Action action) => TryPost(action, null);

    private bool TryPost(Action action, Action<Exception>? abandon)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return false;
            }

            _queue.Add((action, abandon));
            return true;
        }
    }

    /// <summary>Run <paramref name="func"/> on the pipeline thread (inline when already on it).</summary>
    public Task<T>? TryRun<T>(Func<T> func)
    {
        if (IsPipelineThread)
        {
            return Task.FromResult(func());
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = TryPost(
            () =>
            {
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            },
            ex => tcs.TrySetException(new InvalidOperationException("The pk command's pipeline stopped before this could run.", ex)));
        return posted ? tcs.Task : null;
    }

    /// <summary>
    /// Process queued work on the pipeline thread until <paramref name="task"/> completes and the queue is drained. If
    /// an action throws (the pipeline was stopped, or WriteError under -ErrorAction Stop), the invocation is closed —
    /// later posts fail and waiting callers get an exception instead of hanging — and the exception propagates.
    /// </summary>
    public void Pump(Task task)
    {
        while (true)
        {
            if (_queue.TryTake(out var item, 30))
            {
                try
                {
                    item.Run();
                }
                catch (Exception ex)
                {
                    Abandon(ex);
                    throw;
                }

                continue;
            }

            if (task.IsCompleted)
            {
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        _completed = true;
                        return;
                    }
                }
            }
        }
    }

    private void Abandon(Exception reason)
    {
        lock (_gate)
        {
            _completed = true;
        }

        while (_queue.TryTake(out var item))
        {
            item.Abandon?.Invoke(reason);
        }
    }

    private sealed class Restore(PkInvocation? previous) : IDisposable
    {
        public void Dispose() => CurrentLocal.Value = previous;
    }
}

/// <summary>
/// Runs PowerShell in the main runspace from any thread without deadlocking: nested when called on the pipeline
/// thread, marshalled to the `pk` pump when a `pk` command is running, otherwise through <see cref="IPickleShell"/>.
/// </summary>
public static class MainRunspace
{
    public static bool IsPipelineThread(PickleRuntime runtime) =>
        runtime.Engine.IsOpen
        && Runspace.DefaultRunspace is { } current
        && ReferenceEquals(current, runtime.Engine.MainRunspace)
        && Runspace.CanUseDefaultRunspace;

    public static Task<ShellResult> InvokeAsync(
        PickleRuntime runtime,
        string script,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (IsPipelineThread(runtime))
        {
            return Task.FromResult(InvokeNested(script, parameters));
        }

        if (PkInvocation.Current is { } invocation && invocation.TryRun(() => InvokeNested(script, parameters)) is { } marshalled)
        {
            return marshalled;
        }

        return runtime.Engine.InvokeAsync(script, parameters, ShellTarget.Main, cancellationToken);
    }

    public static ShellResult Invoke(PickleRuntime runtime, string script, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default) =>
        InvokeAsync(runtime, script, parameters, cancellationToken).GetAwaiter().GetResult();

    private static ShellResult InvokeNested(string script, IReadOnlyDictionary<string, object?>? parameters)
    {
        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
        ps.AddScript(script, useLocalScope: parameters is { Count: > 0 });
        foreach (var (name, value) in parameters ?? new Dictionary<string, object?>())
        {
            ps.AddParameter(name, value);
        }

        try
        {
            var output = ps.Invoke();
            return new ShellResult([.. output], [.. ps.Streams.Error]);
        }
        catch (RuntimeException ex)
        {
            return new ShellResult([], [ex.ErrorRecord]);
        }
    }
}
