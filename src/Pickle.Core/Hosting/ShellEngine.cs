using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using Pickle.Abstractions;

namespace Pickle.Core.Hosting;

public sealed record ExecutionResult(bool Success, int? ExitCode, TimeSpan Duration, bool Interrupted);

/// <summary>Queued edit for the line editor (from panels/plugins via IPickleShell).</summary>
public sealed record EditorRequest(bool Replace, string Text);

/// <summary>
/// Owns the main runspace (interactive) and a lazy background runspace pool. Implements <see cref="IPickleShell"/>.
/// Threading: the REPL thread runs interactive pipelines synchronously. Calls from inside a running pipeline
/// (e.g. a `pk` cmdlet) execute as nested pipelines; calls from other threads wait until the runspace is idle.
/// </summary>
public sealed class ShellEngine : IPickleShell, IDisposable
{
    private readonly PickleRuntime _runtime;
    private static readonly object OpenGate = new();
    private readonly SemaphoreSlim _mainLock = new(1, 1);
    private readonly object _currentGate = new();
    private InitialSessionState? _sessionState;
    private RunspacePool? _pool;
    private PowerShell? _current;
    private string _cwd = Environment.CurrentDirectory;

    public ShellEngine(PickleRuntime runtime)
    {
        _runtime = runtime;
        Host = new PickleHost(runtime);
    }

    public PickleHost Host { get; }

    public Runspace MainRunspace { get; private set; } = null!;

    public bool IsOpen => MainRunspace is { RunspaceStateInfo.State: RunspaceState.Opened };

    public bool IsExecuting { get; private set; }

    public ConcurrentQueue<EditorRequest> EditorRequests { get; } = new();

    public ConcurrentQueue<string> SubmittedCommands { get; } = new();

    /// <summary>Panels requested while a pipeline was running; the line editor opens them when the prompt is back.</summary>
    public ConcurrentQueue<(PanelDescriptor Panel, string? Argument)> PendingPanels { get; } = new();

    public bool IsBusy => IsExecuting;

    public ExecutionResult? LastResult { get; private set; }

    public string CurrentDirectory => _cwd;

    public bool IsInteractive => _runtime.Terminal.IsInteractive && !_runtime.Options.Headless;

    // ───────────── Lifecycle ─────────────

    public void Open()
    {
        var iss = InitialSessionState.CreateDefault2();
        if (OperatingSystem.IsWindows())
        {
            // Same default as pwsh on Windows; lets profile.ps1 and local module scripts run.
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.RemoteSigned;
        }

        iss.ThreadOptions = PSThreadOptions.ReuseThread;
        iss.Variables.Add(new SessionStateVariableEntry("PickleHome", _runtime.Paths.ConfigDir, "Pickle config directory", ScopedItemOptions.ReadOnly));
        iss.Variables.Add(new SessionStateVariableEntry("PickleVersion", PickleRuntime.Version, "Pickle version", ScopedItemOptions.ReadOnly));

        foreach (var cmdlet in DiscoverCmdlets(typeof(ShellEngine).Assembly))
        {
            iss.Commands.Add(cmdlet);
        }

        var modulesDir = EmbeddedModules.Extract(_runtime.Paths, _runtime.Log);
        PrependModulePath(modulesDir);

        foreach (var contributor in _runtime.SessionContributors)
        {
            contributor.Contribute(iss);
        }

        _sessionState = iss;
        MainRunspace = RunspaceFactory.CreateRunspace(Host, iss);
        MainRunspace.Name = "Pickle";

        // PowerShell's first-time provider initialization isn't thread-safe: runspaces opened concurrently in one
        // process (tests) could come up without the FileSystem provider.
        lock (OpenGate)
        {
            MainRunspace.Open();
        }

        Runspace.DefaultRunspace = MainRunspace;
        MainRunspace.Debugger.DebuggerStop += (_, e) => _runtime.Repl.OnDebuggerStop(e);

        // Import the core Pickle module (aliases pk/pickle, helper functions).
        InvokeSilently("param($path) Import-Module -Name $path -Global", new Dictionary<string, object?> { ["path"] = Path.Combine(modulesDir, "Pickle", "Pickle.psd1") });
        RefreshCwd();
    }

    public void Dispose()
    {
        _pool?.Dispose();
        if (MainRunspace is not null)
        {
            MainRunspace.Dispose();
        }

        _mainLock.Dispose();
    }

    // ───────────── Interactive execution ─────────────

    /// <summary>Run a line typed by the user: output goes through Out-Default so native programs own the console.</summary>
    public ExecutionResult ExecuteInteractive(string commandLine)
    {
        var sw = Stopwatch.StartNew();
        var interrupted = false;
        _mainLock.Wait();
        try
        {
            IsExecuting = true;
            using var ps = PowerShell.Create();
            ps.Runspace = Host.Runspace;
            ps.AddScript(commandLine);
            ps.Commands.Commands[0].MergeMyResults(PipelineResultTypes.Error, PipelineResultTypes.Output);
            ps.AddCommand("Out-Default");

            lock (_currentGate)
            {
                _current = ps;
            }

            try
            {
                ps.Invoke(null, new PSInvocationSettings { AddToHistory = true });
                interrupted = ps.InvocationStateInfo.State == PSInvocationState.Stopped;
            }
            catch (PipelineStoppedException)
            {
                interrupted = true;
            }
            catch (RuntimeException ex)
            {
                ReportException(ex.ErrorRecord);
            }
            finally
            {
                lock (_currentGate)
                {
                    _current = null;
                }

                Host.PickleUI.ResetProgress();
            }
        }
        finally
        {
            IsExecuting = false;
            _mainLock.Release();
        }

        var (success, exitCode) = QueryStatus();
        RefreshCwd();
        sw.Stop();
        LastResult = new ExecutionResult(success && !interrupted, exitCode, sw.Elapsed, interrupted);
        return LastResult;
    }

    /// <summary>
    /// Take exclusive use of the main runspace without running a pipeline through the engine (e.g. tab completion).
    /// Returns false if it is busy. Always pair with <see cref="ExitMain"/>.
    /// </summary>
    internal bool TryEnterMain(TimeSpan timeout) => _mainLock.Wait(timeout);

    internal void ExitMain() => _mainLock.Release();

    /// <summary>Stop the running interactive pipeline (Ctrl+C).</summary>
    public bool StopCurrent()
    {
        lock (_currentGate)
        {
            if (_current is null)
            {
                return false;
            }

            _current.BeginStop(null, null);
            return true;
        }
    }

    /// <summary>Run a script in the main runspace with no output to the terminal. Returns output objects.</summary>
    public IReadOnlyList<PSObject> InvokeSilently(string script, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var result = InvokeMain(script, parameters);
        foreach (var error in result.Errors)
        {
            _runtime.Log.Warn("engine", $"Silent script error: {error}");
        }

        return result.Output;
    }

    private void ReportException(ErrorRecord record)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = Host.Runspace;
            ps.AddCommand("Out-Default");
            ps.Invoke(new object[] { record });
        }
        catch (Exception ex) when (ex is RuntimeException or InvalidOperationException)
        {
            Host.UI.WriteErrorLine(record.ToString());
        }
    }

    /// <summary>Number of running PowerShell jobs, refreshed after every interactive command.</summary>
    public int RunningJobCount { get; private set; }

    private (bool Success, int? ExitCode) QueryStatus()
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = Host.Runspace;

            // $? must be read first: it still holds the previous pipeline's status.
            ps.AddScript("$?; $global:LASTEXITCODE; @(Get-Job -State Running -ErrorAction Ignore).Count");
            var results = ps.Invoke();
            var success = results.Count > 0 && results[0]?.BaseObject is bool b && b;
            int? exit = results.Count > 1 && results[1]?.BaseObject is int code ? code : null;
            RunningJobCount = results.Count > 2 && results[2]?.BaseObject is int jobs ? jobs : 0;
            return (success, exit);
        }
        catch (Exception ex) when (ex is RuntimeException or InvalidOperationException)
        {
            return (false, null);
        }
    }

    private void RefreshCwd()
    {
        try
        {
            if (Host.IsRunspacePushed)
            {
                return;
            }

            var path = MainRunspace.SessionStateProxy.Path.CurrentFileSystemLocation.ProviderPath;
            if (!string.IsNullOrEmpty(path))
            {
                _cwd = path;
                if (Directory.Exists(path))
                {
                    Environment.CurrentDirectory = path;
                }
            }
        }
        catch (Exception ex) when (ex is PSInvalidOperationException or InvalidOperationException or IOException)
        {
            _runtime.Log.Debug("engine", $"cwd refresh failed: {ex.Message}");
        }
    }

    // ───────────── IPickleShell ─────────────

    public Task<ShellResult> InvokeAsync(string script, IReadOnlyDictionary<string, object?>? parameters = null, ShellTarget target = ShellTarget.Main, CancellationToken cancellationToken = default)
    {
        if (target == ShellTarget.Background)
        {
            return InvokeBackgroundAsync(script, parameters, cancellationToken);
        }

        // Called from inside a running pipeline in the main runspace (same thread): run nested.
        if (IsOnPipelineThread)
        {
            return Task.FromResult(InvokeNested(script, parameters));
        }

        // Another thread during a `pk` command: the command's pipeline holds the runspace, so hand the work to its pump
        // instead of waiting for the lock it holds.
        if (IsExecuting && Commands.PkInvocation.Current is { } invocation
            && invocation.TryRun(() => InvokeNested(script, parameters)) is { } marshalled)
        {
            return marshalled;
        }

        return Task.Run(() => InvokeMain(script, parameters, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// The calling thread is running a pipeline in the main runspace (interactive or InvokeAsync). Work from here must
    /// nest: waiting for the runspace lock would wait on ourselves.
    /// </summary>
    private bool IsOnPipelineThread =>
        MainRunspace is not null && Runspace.DefaultRunspace == MainRunspace && Runspace.CanUseDefaultRunspace;

    private ShellResult InvokeMain(string script, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken = default)
    {
        if (IsOnPipelineThread)
        {
            return InvokeNested(script, parameters);
        }

        _mainLock.Wait(cancellationToken);
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = MainRunspace;
            return Run(ps, script, parameters, cancellationToken);
        }
        finally
        {
            _mainLock.Release();
        }
    }

    private static ShellResult InvokeNested(string script, IReadOnlyDictionary<string, object?>? parameters)
    {
        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
        return Run(ps, script, parameters, CancellationToken.None);
    }

    private async Task<ShellResult> InvokeBackgroundAsync(string script, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        var pool = EnsurePool();
        using var ps = PowerShell.Create();
        ps.RunspacePool = pool;
        AddScriptWithParameters(ps, script, parameters);
        await using var registration = cancellationToken.Register(() => ps.BeginStop(null, null));
        try
        {
            var output = await ps.InvokeAsync().ConfigureAwait(false);
            return new ShellResult([.. output], [.. ps.Streams.Error]);
        }
        catch (PipelineStoppedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (RuntimeException ex)
        {
            return new ShellResult([], [ex.ErrorRecord]);
        }
    }

    private static ShellResult Run(PowerShell ps, string script, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        AddScriptWithParameters(ps, script, parameters);
        using var registration = cancellationToken.Register(() => ps.BeginStop(null, null));
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

    private static void AddScriptWithParameters(PowerShell ps, string script, IReadOnlyDictionary<string, object?>? parameters)
    {
        ps.AddScript(script, useLocalScope: parameters is { Count: > 0 });
        if (parameters is null)
        {
            return;
        }

        foreach (var (name, value) in parameters)
        {
            ps.AddParameter(name, value);
        }
    }

    private RunspacePool EnsurePool()
    {
        if (_pool is not null)
        {
            return _pool;
        }

        lock (_currentGate)
        {
            if (_pool is null)
            {
                var iss = _sessionState ?? InitialSessionState.CreateDefault2();
                var pool = RunspaceFactory.CreateRunspacePool(iss);
                pool.SetMaxRunspaces(4);
                pool.Open();
                _pool = pool;
            }
        }

        return _pool;
    }

    public void InsertText(string text) => EditorRequests.Enqueue(new EditorRequest(false, text));

    public void ReplaceInput(string text) => EditorRequests.Enqueue(new EditorRequest(true, text));

    public void SubmitCommand(string commandLine) => SubmittedCommands.Enqueue(commandLine);

    public void SetLocation(string path)
    {
        var result = InvokeMain("param($p) Set-Location -LiteralPath $p", new Dictionary<string, object?> { ["p"] = path });
        foreach (var error in result.Errors)
        {
            Host.UI.WriteErrorLine(error.ToString());
        }

        RefreshCwd();
    }

    public void WriteLine(string text) => _runtime.Terminal.Write(text + "\n");

    public void OpenPanelWhenIdle(PanelDescriptor panel, string? argument = null) => PendingPanels.Enqueue((panel, argument));

    // ───────────── Helpers ─────────────

    private static IEnumerable<SessionStateCmdletEntry> DiscoverCmdlets(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(Cmdlet).IsAssignableFrom(type))
            {
                continue;
            }

            var attr = type.GetCustomAttribute<CmdletAttribute>();
            if (attr is null)
            {
                continue;
            }

            var entry = new SessionStateCmdletEntry($"{attr.VerbName}-{attr.NounName}", type, helpFileName: null);
            yield return entry;
        }
    }

    private static void PrependModulePath(string dir)
    {
        var current = Environment.GetEnvironmentVariable("PSModulePath") ?? string.Empty;
        var parts = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        parts.RemoveAll(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase));
        parts.Insert(0, dir);

        // The SDK's built-in modules (Utility, Management, Security...) live under runtimes/<os>/lib/<tfm>/Modules.
        // PowerShell finds them next to System.Management.Automation.dll, but not in a single-file bundle where
        // the assembly has no location, so add the directory explicitly.
        var insertAt = 1;
        foreach (var bundled in FindBundledModulesDirectories())
        {
            if (!parts.Contains(bundled, StringComparer.OrdinalIgnoreCase))
            {
                parts.Insert(insertAt++, bundled);
            }
        }

        // If pwsh 7 is installed, make its bundled modules (PSResourceGet, ThreadJob, Archive...) discoverable too.
        var pwshModules = FindPwshModulesDirectory();
        if (pwshModules is not null && !parts.Contains(pwshModules, StringComparer.OrdinalIgnoreCase))
        {
            parts.Add(pwshModules);
        }

        Environment.SetEnvironmentVariable("PSModulePath", string.Join(Path.PathSeparator, parts));
    }

    /// <summary>SDK built-ins (runtimes/&lt;os&gt;/…/Modules) and modules bundled by BundledModules.targets (Modules/).</summary>
    private static IEnumerable<string> FindBundledModulesDirectories()
    {
        var os = OperatingSystem.IsWindows() ? "win" : "unix";
        return new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtimes", os, "lib", "net10.0", "Modules"),
            Path.Combine(AppContext.BaseDirectory, "Modules"),
        }.Where(Directory.Exists);
    }

    private static string? FindPwshModulesDirectory()
    {
        var exe = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, exe);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var resolved = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
                var modules = Path.Combine(Path.GetDirectoryName(resolved)!, "Modules");
                if (Directory.Exists(modules))
                {
                    return modules;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }

        return null;
    }
}
