using System.Collections.Concurrent;
using System.Management.Automation;
using Pickle.Abstractions;

namespace Pickle.Core.Syntax;

/// <summary>
/// Names the highlighter treats as runnable commands: session commands (functions, aliases, imported cmdlets),
/// commands exported by installed modules (auto-loadable), and executables/scripts on PATH. Everything loads in the
/// background; until the first load completes every name counts as known so nothing flashes red at startup.
/// Relative/absolute paths are checked against the file system on demand.
/// </summary>
internal sealed class CommandCache : IDisposable
{
    private const string SessionScript = "(Get-Command -ListImported -CommandType Alias,Function,Filter,Cmdlet,Configuration -ErrorAction Ignore).Name";

    // Applications are scanned natively from PATH (much faster, and rescanned when PATH changes).
    private const string ModuleScript = "(Get-Command -CommandType Alias,Function,Filter,Cmdlet,Configuration -ErrorAction Ignore).Name";

    private static readonly HashSet<string> Empty = new(StringComparer.OrdinalIgnoreCase);

    private readonly PickleRuntime _runtime;
    private readonly CancellationTokenSource _disposed = new();
    private readonly CancellationToken _disposedToken;
    private readonly ConcurrentDictionary<string, bool> _pathChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private HashSet<string> _session = Empty;
    private HashSet<string> _modules = Empty;
    private HashSet<string> _path = Empty;
    private string? _pathValue;
    private Task? _initial;
    private Task? _sessionRefresh;
    private bool _sessionRefreshQueued;
    private volatile bool _ready;
    private int _version;

    public CommandCache(PickleRuntime runtime)
    {
        _runtime = runtime;
        _disposedToken = _disposed.Token;
    }

    public bool IsReady => _ready;

    /// <summary>Changes whenever a load completes (highlighting results depend on it).</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Starts the initial load if needed (once the runspace is open) and returns it.</summary>
    public Task EnsureStarted()
    {
        if (!_runtime.Engine.IsOpen)
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            return _initial ??= Task.Run(LoadAllAsync);
        }
    }

    /// <summary>After a command ran: functions/aliases/PATH may have changed and files may have been created.</summary>
    public void OnCommandFinished()
    {
        _pathChecks.Clear();
        if (_initial is null || _disposedToken.IsCancellationRequested)
        {
            return;
        }

        lock (_gate)
        {
            if (_sessionRefresh is { IsCompleted: false })
            {
                _sessionRefreshQueued = true;
                return;
            }

            _sessionRefresh = Task.Run(RefreshAfterCommandAsync);
        }
    }

    public void OnDirectoryChanged() => _pathChecks.Clear();

    public bool IsKnown(string name, string cwd)
    {
        if (!_ready || name.Length == 0)
        {
            return true;
        }

        if (_session.Contains(name) || _modules.Contains(name) || _path.Contains(name))
        {
            return true;
        }

        if (!LooksLikePath(name))
        {
            return false;
        }

        if (_pathChecks.TryGetValue(cwd + "\u0000" + name, out var known))
        {
            return known;
        }

        known = PathExists(name, cwd);

        // Module-qualified names (Microsoft.PowerShell.Management\Get-ChildItem).
        var slash = name.LastIndexOf('\\');
        if (!known && slash > 0 && slash < name.Length - 1 && !name.Contains('/', StringComparison.Ordinal))
        {
            var bare = name[(slash + 1)..];
            known = _session.Contains(bare) || _modules.Contains(bare);
        }

        _pathChecks[cwd + "\u0000" + name] = known;
        return known;
    }

    public void Dispose() => _disposed.Cancel();

    private async Task LoadAllAsync()
    {
        try
        {
            _path = ScanPath(out _pathValue);
            var session = await QueryAsync(SessionScript, ShellTarget.Main).ConfigureAwait(false);
            var modules = QueryModules();
            if (session is null || modules is null)
            {
                return;
            }

            _session = session;
            _modules = modules;
            _ready = true;
            Interlocked.Increment(ref _version);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _runtime.Log.Debug("highlight", $"command cache load failed: {ex.Message}");
        }
    }

    private async Task RefreshAfterCommandAsync()
    {
        while (true)
        {
            try
            {
                if (!string.Equals(Environment.GetEnvironmentVariable("PATH"), _pathValue, StringComparison.Ordinal))
                {
                    _path = ScanPath(out _pathValue);
                }

                if (await QueryAsync(SessionScript, ShellTarget.Main).ConfigureAwait(false) is { } session)
                {
                    _session = session;
                }

                Interlocked.Increment(ref _version);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _runtime.Log.Debug("highlight", $"command cache refresh failed: {ex.Message}");
            }

            lock (_gate)
            {
                if (!_sessionRefreshQueued)
                {
                    return;
                }

                _sessionRefreshQueued = false;
            }
        }
    }

    private async Task<HashSet<string>?> QueryAsync(string script, ShellTarget target)
    {
        if (_disposedToken.IsCancellationRequested || !_runtime.Engine.IsOpen)
        {
            return null;
        }

        var result = await _runtime.Engine.InvokeAsync(script, null, target, _disposedToken).ConfigureAwait(false);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in result.Output)
        {
            if (item?.BaseObject is string name)
            {
                names.Add(name);
            }
            else if (item?.BaseObject is IEnumerable<object> many)
            {
                foreach (var n in many.OfType<string>())
                {
                    names.Add(n);
                }
            }
        }

        return names;
    }

    // A private runspace: listing every module's commands takes seconds and must not hold the main runspace.
    private HashSet<string>? QueryModules()
    {
        if (_disposedToken.IsCancellationRequested)
        {
            return null;
        }

        using var ps = PowerShell.Create(System.Management.Automation.Runspaces.InitialSessionState.CreateDefault2());
        using var registration = _disposedToken.Register(() => ps.BeginStop(null, null));
        ps.AddScript(ModuleScript);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ps.Invoke())
        {
            if (item?.BaseObject is string name)
            {
                names.Add(name);
            }
        }

        return names;
    }

    internal static HashSet<string> ScanPath(out string? pathValue)
    {
        pathValue = Environment.GetEnvironmentVariable("PATH");
        return ScanPath(pathValue);
    }

    internal static HashSet<string> ScanPath(string? pathValue)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Append(".PS1")
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        foreach (var dir in (pathValue ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var fileName = Path.GetFileName(file);
                    var extension = Path.GetExtension(fileName);
                    if (extensions is not null)
                    {
                        if (extensions.Contains(extension))
                        {
                            names.Add(fileName);
                            names.Add(Path.GetFileNameWithoutExtension(fileName));
                        }
                    }
                    else if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                    {
                        names.Add(fileName);
                        names.Add(Path.GetFileNameWithoutExtension(fileName));
                    }
                    else if (Commands.ExecutableLocator.IsExecutableFile(file))
                    {
                        names.Add(fileName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }

        return names;
    }

    private static bool LooksLikePath(string name) =>
        name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal)
        || name.StartsWith('.') || name.StartsWith('~') || (name.Length >= 2 && name[1] == ':');

    private static bool PathExists(string name, string cwd)
    {
        try
        {
            var path = name;
            if (path.StartsWith('~'))
            {
                path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
            }

            if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(cwd, path);
            }

            if (File.Exists(path))
            {
                return true;
            }

            if (OperatingSystem.IsWindows() && !Path.HasExtension(path))
            {
                foreach (var ext in (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries).Append(".ps1"))
                {
                    if (File.Exists(path + ext))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        return false;
    }
}
