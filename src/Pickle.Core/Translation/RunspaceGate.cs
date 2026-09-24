using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Pickle.Abstractions;

namespace Pickle.Core.Translation;

/// <summary>
/// Deadlock-free access to the main runspace for components whose code can run on three kinds of threads: the pipeline
/// thread (inside a cmdlet → nested pipeline), an idle caller (REPL/hook/test thread → main runspace), or a worker thread
/// while a pipeline runs (e.g. a `pk` command's task) where the main runspace can't be entered — callers defer that work
/// to the next PostExecute/Prompt hook, or use the background pool for read-only queries.
/// </summary>
public static class RunspaceGate
{
    public static bool OnPipelineThread(PickleRuntime runtime) =>
        runtime.Engine.IsOpen && Runspace.DefaultRunspace == runtime.Engine.MainRunspace && Runspace.CanUseDefaultRunspace;

    public static bool CanInvoke(PickleRuntime runtime) =>
        runtime.Engine.IsOpen
        && (OnPipelineThread(runtime) || runtime.Engine.MainRunspace.RunspaceAvailability == RunspaceAvailability.Available);

    /// <summary>Runs in the main runspace if that's possible from this thread; returns false (nothing run) otherwise.</summary>
    public static bool TryInvoke(PickleRuntime runtime, string script, IReadOnlyDictionary<string, object?>? parameters, out ShellResult result)
    {
        result = ShellResult.Empty;
        if (!runtime.Engine.IsOpen)
        {
            return false;
        }

        if (OnPipelineThread(runtime))
        {
            result = InvokeNested(script, parameters);
            return true;
        }

        if (runtime.Engine.MainRunspace.RunspaceAvailability != RunspaceAvailability.Available)
        {
            return false;
        }

        result = runtime.Engine.InvokeAsync(script, parameters).GetAwaiter().GetResult();
        return true;
    }

    /// <summary>Read-only query: main runspace when reachable, otherwise the background pool (same initial session state).</summary>
    public static ShellResult Query(PickleRuntime runtime, string script, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (TryInvoke(runtime, script, parameters, out var result))
        {
            return result;
        }

        if (!runtime.Engine.IsOpen)
        {
            return ShellResult.Empty;
        }

        try
        {
            return runtime.Engine.InvokeAsync(script, parameters, ShellTarget.Background).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or RuntimeException)
        {
            runtime.Log.Debug("engine", $"Background pool unavailable ({ex.Message}); using a temporary runspace");
            return InvokeIsolated(script, parameters);
        }
    }

    private static ShellResult InvokeIsolated(string script, IReadOnlyDictionary<string, object?>? parameters)
    {
        using var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript(script, useLocalScope: true);
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

    private static ShellResult InvokeNested(string script, IReadOnlyDictionary<string, object?>? parameters)
    {
        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
        ps.AddScript(script, useLocalScope: parameters is { Count: > 0 });
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                ps.AddParameter(name, value);
            }
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
