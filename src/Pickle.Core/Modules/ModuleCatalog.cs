using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Modules;

/// <summary>
/// The optional modules and which are on: the installer's machine-wide selection, plus what this user turned on
/// (<c>modules.enabled</c>), minus what they turned off (<c>modules.disabled</c>). Modules that are off are never
/// constructed.
/// </summary>
public sealed class ModuleCatalog(PickleRuntime runtime) : IModuleCatalog
{
    private IReadOnlyList<ModuleDescriptor> _available = [];
    private IReadOnlySet<string>? _machine;

    public IReadOnlyList<ModuleDescriptor> Available => _available;

    internal void SetAvailable(IReadOnlyList<ModuleDescriptor> modules)
    {
        _available = [.. modules.OrderBy(m => m.Hidden).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The modules the installer selected (read once per session).</summary>
    public IReadOnlySet<string> MachineSelection => _machine ??= MachineModules.Read();

    /// <summary>Replaces what the installer selected (tests, so a developer's own installation does not leak in).</summary>
    public void UseMachineSelection(IEnumerable<string> ids) => _machine = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled(string id) => Source(id) != "off";

    public ModuleStatus? Find(string id) => Status().FirstOrDefault(s => string.Equals(s.Module.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<ModuleStatus> Status() => [.. _available.Select(Describe)];

    /// <summary>Loads every module that is on and supported here (called once at startup, after the built-in plugins).</summary>
    internal void LoadEnabled()
    {
        foreach (var module in _available.Where(m => m.IsSupportedHere && IsEnabled(m.Id)))
        {
            runtime.PluginManager.LoadModule(module);
        }
    }

    public ModuleStatus Enable(string id)
    {
        var module = Require(id);
        runtime.Config.Update(c =>
        {
            c.Modules.Disabled.RemoveAll(d => Same(d, module.Id));
            if (!MachineSelection.Contains(module.Id) && !c.Modules.Enabled.Any(e => Same(e, module.Id)))
            {
                c.Modules.Enabled.Add(module.Id);
            }
        });
        if (module.IsSupportedHere)
        {
            runtime.PluginManager.LoadModule(module);
        }

        return Describe(module);
    }

    public ModuleStatus Disable(string id)
    {
        var module = Require(id);
        runtime.Config.Update(c =>
        {
            c.Modules.Enabled.RemoveAll(e => Same(e, module.Id));
            if (MachineSelection.Contains(module.Id) && !c.Modules.Disabled.Any(d => Same(d, module.Id)))
            {
                c.Modules.Disabled.Add(module.Id);
            }
        });
        return Describe(module);
    }

    public void Select(IReadOnlyCollection<string> ids)
    {
        var wanted = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        foreach (var module in _available)
        {
            if (wanted.Contains(module.Id))
            {
                Enable(module.Id);
            }
            else if (IsEnabled(module.Id))
            {
                Disable(module.Id);
            }
        }
    }

    private ModuleStatus Describe(ModuleDescriptor module)
    {
        var source = Source(module.Id);
        var info = runtime.PluginManager.Plugins.LastOrDefault(p => p.Kind == Plugins.PluginInfo.ModuleKind && string.Equals(p.Id, module.Id, StringComparison.OrdinalIgnoreCase));
        var enabled = source != "off";
        var state = info?.Status switch
        {
            Plugins.PluginStatus.Loaded => ModuleState.Loaded,
            Plugins.PluginStatus.Failed => ModuleState.Failed,
            _ when enabled && !module.IsSupportedHere => ModuleState.Unsupported,
            _ when enabled => ModuleState.Pending,
            _ => ModuleState.Off,
        };
        return new ModuleStatus(module, enabled, state, source, info?.Error);
    }

    /// <summary>"installer", "user", "installer+user" or "off".</summary>
    private string Source(string id)
    {
        var config = runtime.Config.Current.Modules;
        if (config.Disabled.Any(d => Same(d, id)))
        {
            return "off";
        }

        var machine = MachineSelection.Contains(id);
        var user = config.Enabled.Any(e => Same(e, id));
        return (machine, user) switch
        {
            (true, true) => "installer+user",
            (true, false) => "installer",
            (false, true) => "user",
            _ => "off",
        };
    }

    private ModuleDescriptor Require(string id) =>
        _available.FirstOrDefault(m => Same(m.Id, id))
        ?? throw new ArgumentException($"There is no module called '{id}'. 'pk module list --all' shows them.");

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
