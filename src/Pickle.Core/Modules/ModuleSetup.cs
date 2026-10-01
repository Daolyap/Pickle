using Pickle.Abstractions;

namespace Pickle.Core.Modules;

/// <summary>The setup question that offers the optional modules (first start, after an upgrade that added some, and <c>pk setup</c>).</summary>
internal static class ModuleSetup
{
    public static FirstRunOffer Offer(PickleRuntime runtime) => new(
        "modules",
        "Choose optional modules (Docker, Kubernetes, nmap, GitHub, secrets vault, …)?",
        () => runtime.Config.Current.Modules.AskAtSetup
            && runtime.Terminal.IsInteractive
            && runtime.ModuleCatalog.Status().Any(s => !s.Module.Hidden && s.Module.IsSupportedHere),
        () =>
        {
            var catalog = runtime.ModuleCatalog;
            var picked = ModulePicker.Run(runtime.Terminal, runtime.Themes.Current.Ui, [.. catalog.Status().Where(s => !s.Module.Hidden)]);
            if (picked is null)
            {
                return "No changes.";
            }

            catalog.Select(picked);
            return picked.Count == 0 ? "No optional modules are on." : $"On: {string.Join(", ", picked.Order(StringComparer.OrdinalIgnoreCase))}. 'pk module' changes this later.";
        })
    {
        Since = 3,
    };
}
