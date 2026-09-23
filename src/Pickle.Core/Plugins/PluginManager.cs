using Pickle.Abstractions;
using Pickle.Core.Contracts;

namespace Pickle.Core.Plugins;

/// <summary>
/// FOUNDATION PLACEHOLDER (workstream W5 completes this file): initializes built-in plugins. W5 adds discovery of
/// PowerShell-module plugins (PrivateData.Pickle) and .NET plugins (AssemblyLoadContext + trust prompt),
/// enable/disable, and `pk plugin`.
/// </summary>
public sealed class PluginManager : IPluginManager
{
    private readonly PickleRuntime _runtime;
    private readonly List<LoadedPlugin> _loaded = [];

    public PluginManager(PickleRuntime runtime) => _runtime = runtime;

    public IReadOnlyList<LoadedPlugin> Loaded => _loaded;

    public void LoadAll(IReadOnlyList<IPicklePlugin> builtIns)
    {
        foreach (var plugin in builtIns)
        {
            try
            {
                plugin.Initialize(_runtime);
                _loaded.Add(new LoadedPlugin(plugin.Id, plugin.DisplayName, "built-in", null, true, null));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _runtime.Log.Error("plugins", $"Built-in plugin {plugin.Id} failed to initialize", ex);
                _loaded.Add(new LoadedPlugin(plugin.Id, plugin.DisplayName, "built-in", null, false, ex.Message));
            }
        }
    }
}
