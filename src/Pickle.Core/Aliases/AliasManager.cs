using System.Text;
using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Core.Contracts;
using Pickle.Core.Translation;

namespace Pickle.Core.Aliases;

/// <summary>
/// Persistent aliases (aliases.json) materialized as global PowerShell functions. Kinds: Simple (body + <c>@args</c>),
/// Parameterized (<c>{name}</c>, <c>{name=default}</c>, <c>{*}</c>) and Script (function body). Directory/machine scoped
/// aliases are (re)defined as the location changes. Materialization runs in the main runspace when that's reachable from
/// the calling thread; otherwise (e.g. from a `pk` command's worker thread) it's applied at the next PostExecute/Prompt hook.
/// </summary>
public sealed class AliasManager : IAliasRegistry, IAliasMaterializer, IRuntimeComponent, IDisposable
{
    private const string MaterializeScript = """
        param($remove, $names, $bodies)
        foreach ($n in $remove) {
            Remove-Item -LiteralPath "Function:\$n" -Force -ErrorAction Ignore
            $shims = Get-Module -Name Pickle.Translate
            if ($shims -and $shims.ExportedFunctions.ContainsKey($n)) {
                Set-Item -LiteralPath "Function:\global:$n" -Value $shims.ExportedFunctions[$n].ScriptBlock -Force
            }
        }
        for ($i = 0; $i -lt $names.Count; $i++) {
            $n = $names[$i]
            # Aliases resolve before functions; AllScope aliases also have a copy in this scope.
            Remove-Alias -Name $n -Force -ErrorAction Ignore
            Remove-Alias -Name $n -Scope Global -Force -ErrorAction Ignore
            Set-Item -LiteralPath "Function:\global:$n" -Value ([scriptblock]::Create($bodies[$i])) -Force
        }
        """;

    private readonly PickleRuntime _runtime;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _defined = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _subscriptions = [];
    private List<AliasDefinition>? _aliases;
    private DateTime? _fileStamp;
    private volatile bool _dirty;
    private bool _started;

    public AliasManager(PickleRuntime runtime) => _runtime = runtime;

    public event EventHandler? Changed;

    /// <summary>Launches an editor on a file and waits (replaceable in tests).</summary>
    internal Func<string, int> EditorLauncher { get; set; } = EditorProcess.Run;

    public IReadOnlyList<AliasDefinition> All
    {
        get
        {
            lock (_gate)
            {
                return [.. Load()];
            }
        }
    }

    public AliasDefinition? Get(string name) => All.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Set(AliasDefinition alias)
    {
        AliasCompiler.Validate(alias);
        lock (_gate)
        {
            var list = Load();
            list.RemoveAll(a => string.Equals(a.Name, alias.Name, StringComparison.OrdinalIgnoreCase));
            alias.UpdatedAt = DateTimeOffset.UtcNow;
            list.Add(alias);
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            Save(list);
        }

        OnChanged();
    }

    /// <summary>
    /// <see cref="Set"/> for user-facing commands: refuses to shadow cmdlets, PowerShell aliases and other functions
    /// unless <paramref name="force"/>. Throws <see cref="InvalidOperationException"/> with a user-facing message.
    /// </summary>
    public void SetChecked(AliasDefinition alias, bool force)
    {
        AliasCompiler.Validate(alias);
        if (!force && Get(alias.Name) is null && DescribeConflict(alias.Name) is { } conflict)
        {
            throw new InvalidOperationException($"'{alias.Name}' is already {conflict}. Use --force (-Force) to shadow it.");
        }

        Set(alias);
    }

    public bool Remove(string name)
    {
        bool removed;
        lock (_gate)
        {
            var list = Load();
            removed = list.RemoveAll(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                Save(list);
            }
        }

        if (removed)
        {
            OnChanged();
        }

        return removed;
    }

    /// <summary>Re-read aliases.json (after an external edit or sync). Invalid entries are skipped and logged.</summary>
    public int Reload()
    {
        int count;
        lock (_gate)
        {
            _aliases = null;
            count = Load().Count;
        }

        OnChanged();
        return count;
    }

    public string BuildAliasScript(string cwd)
    {
        var sb = new StringBuilder();
        foreach (var alias in Active(cwd))
        {
            sb.Append("Remove-Item -LiteralPath 'Alias:\\").Append(alias.Name).AppendLine("' -Force -ErrorAction Ignore");
            sb.AppendLine(AliasCompiler.BuildFunction(alias));
        }

        return sb.ToString();
    }

    public void Initialize() => _runtime.CommandRegistry.Register(new AliasCommand(this));

    public void OnStarted()
    {
        _started = true;
        _subscriptions.Add(_runtime.Hooks.Register(HookKind.DirectoryChanged, OnIdle));
        _subscriptions.Add(_runtime.Hooks.Register(HookKind.PostExecute, OnIdle));
        _subscriptions.Add(_runtime.Hooks.Register(HookKind.Prompt, OnIdle));
        _runtime.Engine.InvokeSilently(
            "Update-TypeData -TypeName 'Pickle.Abstractions.AliasDefinition' -DefaultDisplayPropertySet Name, Kind, Body, Description -Force -ErrorAction Ignore");
        Materialize();
    }

    /// <summary>Define/remove functions so the session matches the active aliases. Returns false if deferred.</summary>
    public bool Materialize()
    {
        if (!_started)
        {
            return false;
        }

        var desired = Active(_runtime.Engine.CurrentDirectory).ToDictionary(a => a.Name, AliasCompiler.BuildFunctionBody, StringComparer.OrdinalIgnoreCase);
        List<string> remove;
        List<KeyValuePair<string, string>> define;
        lock (_gate)
        {
            remove = [.. _defined.Keys.Where(n => !desired.ContainsKey(n))];
            define = [.. desired.Where(kv => !_defined.TryGetValue(kv.Key, out var body) || body != kv.Value)];
        }

        if (remove.Count == 0 && define.Count == 0)
        {
            _dirty = false;
            return true;
        }

        var parameters = new Dictionary<string, object?>
        {
            ["remove"] = remove.ToArray(),
            ["names"] = define.Select(kv => kv.Key).ToArray(),
            ["bodies"] = define.Select(kv => kv.Value).ToArray(),
        };

        if (!RunspaceGate.TryInvoke(_runtime, MaterializeScript, parameters, out var result))
        {
            _dirty = true;
            return false;
        }

        foreach (var error in result.Errors)
        {
            _runtime.Log.Warn("aliases", $"Materializing aliases: {error}");
        }

        lock (_gate)
        {
            foreach (var name in remove)
            {
                _defined.Remove(name);
            }

            foreach (var (name, body) in define)
            {
                _defined[name] = body;
            }
        }

        _dirty = false;
        return true;
    }

    /// <summary>What an existing command of this name is ("an existing cmdlet", …), or null if an alias may take the name.</summary>
    internal string? DescribeConflict(string name)
    {
        var result = RunspaceGate.Query(
            _runtime,
            "param($n) Get-Command -Name $n -All -ErrorAction Ignore | ForEach-Object { '{0}|{1}|{2}' -f $_.CommandType, $_.ModuleName, $_.Definition }",
            new Dictionary<string, object?> { ["n"] = name });
        foreach (var line in result.Output.Select(o => o?.ToString() ?? string.Empty))
        {
            var parts = line.Split('|', 3);
            var module = parts.Length > 1 ? parts[1] : string.Empty;
            switch (parts[0])
            {
                case "Alias":
                    return $"a PowerShell alias for {(parts.Length > 2 ? parts[2] : "another command")}";
                case "Cmdlet":
                    return "an existing cmdlet" + (module.Length > 0 ? $" ({module})" : string.Empty);
                case "Function" or "Filter" when module != TranslationPipeline.ModuleName && !_defined.ContainsKey(name):
                    return "an existing function" + (module.Length > 0 ? $" ({module})" : string.Empty);
            }
        }

        return null;
    }

    internal bool Edit(Action<string> report)
    {
        var file = _runtime.Paths.AliasesFile;
        lock (_gate)
        {
            if (!File.Exists(file))
            {
                Save(Load());
            }
        }

        var exit = EditorLauncher(file);
        if (exit != 0)
        {
            report($"Editor exited with code {exit}.");
        }

        var count = Reload();
        report($"Reloaded {count} alias{(count == 1 ? string.Empty : "es")} from {file}.");
        return exit == 0;
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }

    private IEnumerable<AliasDefinition> Active(string cwd) =>
        All.Where(a => AliasNames.IsValid(a.Name) && AliasScope.IsActive(a, cwd, Environment.MachineName));

    private void OnChanged()
    {
        _dirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
        Materialize();
    }

    private ValueTask OnIdle(HookEvent hookEvent, CancellationToken cancellationToken)
    {
        if (ChangedOnDisk())
        {
            Reload();
        }
        else if (_dirty || hookEvent.Kind == HookKind.DirectoryChanged || All.Any(a => !string.IsNullOrWhiteSpace(a.DirectoryScope)))
        {
            Materialize();
        }

        return ValueTask.CompletedTask;
    }

    private bool ChangedOnDisk()
    {
        var file = _runtime.Paths.AliasesFile;
        DateTime? stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : null;
        lock (_gate)
        {
            return _aliases is not null && stamp != _fileStamp;
        }
    }

    private List<AliasDefinition> Load()
    {
        if (_aliases is not null)
        {
            return _aliases;
        }

        _aliases = [];
        var file = _runtime.Paths.AliasesFile;
        _fileStamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : null;
        if (_fileStamp is null)
        {
            return _aliases;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<List<AliasDefinition>>(File.ReadAllText(file), PickleJson.Options) ?? [];
            foreach (var alias in loaded)
            {
                try
                {
                    AliasCompiler.Validate(alias);
                    _aliases.RemoveAll(a => string.Equals(a.Name, alias.Name, StringComparison.OrdinalIgnoreCase));
                    _aliases.Add(alias);
                }
                catch (ArgumentException ex)
                {
                    _runtime.Log.Warn("aliases", $"Skipping alias in aliases.json: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _runtime.Log.Error("aliases", "aliases.json could not be read", ex);
        }

        return _aliases;
    }

    private void Save(List<AliasDefinition> list)
    {
        var file = _runtime.Paths.AliasesFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, PickleJson.Options));
        File.Move(tmp, file, overwrite: true);
        _fileStamp = File.GetLastWriteTimeUtc(file);
    }
}
