using Pickle.Abstractions;

namespace Pickle.Tui.Panels.ListPanel;

/// <summary>
/// Turns <see cref="ListPanelSpec"/>s (PowerShell plugins, <c>Register-PicklePanel</c>) into panel descriptors so they
/// get a <c>panel.&lt;id&gt;</c> action, their DefaultKey and a palette entry. <see cref="IPanelRegistry"/> has no
/// "list registered" event, so this polls: at startup, before every prompt, and when a panel id is not found.
/// </summary>
internal sealed class ListPanelSync
{
    private readonly IPickleContext _pickle;
    private readonly Dictionary<string, ListPanelSpec> _converted = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ListPanelSync(IPickleContext pickle) => _pickle = pickle;

    /// <summary>Registers descriptors for list panels that are new or were re-registered. Returns how many.</summary>
    public int Sync()
    {
        var registered = 0;
        foreach (var spec in _pickle.Panels.ListPanels)
        {
            lock (_gate)
            {
                if (_converted.TryGetValue(spec.Id, out var existing) && ReferenceEquals(existing, spec))
                {
                    continue;
                }

                _converted[spec.Id] = spec;
            }

            _pickle.Panels.Register(PluginListPanel.CreateDescriptor(spec));
            registered++;
        }

        return registered;
    }
}
