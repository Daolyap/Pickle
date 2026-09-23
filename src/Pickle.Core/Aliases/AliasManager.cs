using System.Text;
using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Core.Contracts;

namespace Pickle.Core.Aliases;

/// <summary>
/// FOUNDATION PLACEHOLDER (workstream W4 completes this file): persists aliases.json and materializes Simple
/// aliases as functions. W4 adds Parameterized/Script kinds, directory/machine scopes, `pk alias`, and the
/// Get/Set/Remove-PickleAlias cmdlets.
/// </summary>
public sealed class AliasManager : IAliasRegistry, IAliasMaterializer, IRuntimeComponent
{
    private readonly PickleRuntime _runtime;
    private readonly object _gate = new();
    private List<AliasDefinition>? _aliases;

    public AliasManager(PickleRuntime runtime) => _runtime = runtime;

    public event EventHandler? Changed;

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
        lock (_gate)
        {
            var list = Load();
            list.RemoveAll(a => string.Equals(a.Name, alias.Name, StringComparison.OrdinalIgnoreCase));
            alias.UpdatedAt = DateTimeOffset.UtcNow;
            list.Add(alias);
            Save(list);
        }

        Changed?.Invoke(this, EventArgs.Empty);
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
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public string BuildAliasScript(string cwd)
    {
        var sb = new StringBuilder();
        foreach (var alias in All.Where(a => a.Kind == AliasKind.Simple))
        {
            sb.Append("function global:").Append(alias.Name).Append(" { ").Append(alias.Body).AppendLine(" @args }");
        }

        return sb.ToString();
    }

    public void OnStarted()
    {
        var script = BuildAliasScript(_runtime.Engine.CurrentDirectory);
        if (script.Length > 0)
        {
            _runtime.Engine.InvokeSilently(script);
        }
    }

    private List<AliasDefinition> Load()
    {
        if (_aliases is not null)
        {
            return _aliases;
        }

        _aliases = [];
        if (File.Exists(_runtime.Paths.AliasesFile))
        {
            try
            {
                _aliases = JsonSerializer.Deserialize<List<AliasDefinition>>(File.ReadAllText(_runtime.Paths.AliasesFile), PickleJson.Options) ?? [];
            }
            catch (JsonException ex)
            {
                _runtime.Log.Error("aliases", "aliases.json is invalid", ex);
            }
        }

        return _aliases;
    }

    private void Save(List<AliasDefinition> list)
    {
        Directory.CreateDirectory(_runtime.Paths.ConfigDir);
        File.WriteAllText(_runtime.Paths.AliasesFile, JsonSerializer.Serialize(list, PickleJson.Options));
    }
}
