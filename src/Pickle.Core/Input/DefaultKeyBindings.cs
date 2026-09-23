using Pickle.Abstractions;

namespace Pickle.Core.Input;

/// <summary>
/// The default chord → action table. Actions themselves are registered by their owners (line editor, completion,
/// panels...). User overrides from config.json "keyBindings" are applied on top.
/// </summary>
public static class DefaultKeyBindings
{
    public static IReadOnlyDictionary<string, string> Table { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = EditorActionNames.AcceptLine,
        ["Shift+Enter"] = EditorActionNames.InsertNewline,
        ["Ctrl+Enter"] = EditorActionNames.InsertNewline,
        ["Ctrl+C"] = EditorActionNames.CancelLine,
        ["Escape"] = EditorActionNames.ClearLine,
        ["LeftArrow"] = EditorActionNames.BackwardChar,
        ["RightArrow"] = EditorActionNames.ForwardChar,
        ["Ctrl+LeftArrow"] = EditorActionNames.BackwardWord,
        ["Ctrl+RightArrow"] = EditorActionNames.ForwardWord,
        ["Alt+B"] = EditorActionNames.BackwardWord,
        ["Alt+F"] = EditorActionNames.ForwardWord,
        ["Home"] = EditorActionNames.BeginningOfLine,
        ["End"] = EditorActionNames.EndOfLine,
        ["Backspace"] = EditorActionNames.BackwardDeleteChar,
        ["Delete"] = EditorActionNames.DeleteChar,
        ["Ctrl+Backspace"] = EditorActionNames.BackwardKillWord,
        ["Ctrl+W"] = EditorActionNames.BackwardKillWord,
        ["Ctrl+Delete"] = EditorActionNames.KillWord,
        ["Ctrl+K"] = EditorActionNames.KillToEnd,
        ["Ctrl+Z"] = EditorActionNames.Undo,
        ["Ctrl+Y"] = EditorActionNames.Redo,
        ["Shift+LeftArrow"] = EditorActionNames.SelectBackwardChar,
        ["Shift+RightArrow"] = EditorActionNames.SelectForwardChar,
        ["Ctrl+Shift+LeftArrow"] = EditorActionNames.SelectBackwardWord,
        ["Ctrl+Shift+RightArrow"] = EditorActionNames.SelectForwardWord,
        ["Shift+Home"] = EditorActionNames.SelectToStart,
        ["Shift+End"] = EditorActionNames.SelectToEnd,
        ["Ctrl+A"] = EditorActionNames.SelectAll,
        ["Ctrl+X"] = EditorActionNames.Cut,
        ["Ctrl+V"] = EditorActionNames.Paste,
        ["UpArrow"] = EditorActionNames.HistoryPrevious,
        ["DownArrow"] = EditorActionNames.HistoryNext,
        ["Ctrl+L"] = EditorActionNames.ClearScreen,
        ["Ctrl+D"] = EditorActionNames.ExitIfEmpty,
        ["Tab"] = EditorActionNames.Complete,
        ["Shift+Tab"] = EditorActionNames.CompletePrevious,
        ["Ctrl+R"] = EditorActionNames.HistorySearch,
        ["F1"] = EditorActionNames.CommandPalette,
        ["Ctrl+P"] = EditorActionNames.CommandPalette,
        ["Ctrl+T"] = EditorActionNames.FilePickerInsert,
        ["Alt+C"] = EditorActionNames.FilePickerCd,
        ["F2"] = EditorActionNames.OpenWizard,
        ["Alt+G"] = EditorActionNames.PanelGit,
        ["Alt+J"] = EditorActionNames.PanelJobs,
        ["Alt+W"] = EditorActionNames.PanelWinget,
        ["Alt+U"] = EditorActionNames.PanelUpdates,
        ["Alt+S"] = EditorActionNames.PanelScheduler,
        ["Alt+,"] = EditorActionNames.PanelSettings,
    };

    /// <summary>Bind defaults, basic editing actions (overridden by the full editor), and user overrides.</summary>
    public static void Apply(PickleRuntime runtime)
    {
        var registry = runtime.KeyBindingRegistry;
        foreach (var (chord, action) in Table)
        {
            registry.Bind(chord, action);
        }

        foreach (var (chord, action) in runtime.Config.Current.KeyBindings)
        {
            if (string.IsNullOrWhiteSpace(action) || action == "none")
            {
                registry.Unbind(chord);
            }
            else
            {
                registry.Bind(chord, action);
            }
        }

        // Any registered panel with a DefaultKey gets an action "panel.<id>" and (if unbound) its chord.
        runtime.PanelRegistry.Registered += (_, panel) => RegisterPanelAction(runtime, panel);
        foreach (var panel in runtime.PanelRegistry.All)
        {
            RegisterPanelAction(runtime, panel);
        }
    }

    private static void RegisterPanelAction(PickleRuntime runtime, PanelDescriptor panel)
    {
        var name = "panel." + panel.Id;
        if (runtime.KeyBindingRegistry.GetAction(name) is null)
        {
            runtime.KeyBindingRegistry.RegisterAction(name, $"Open {panel.Title}", (buffer, _) =>
            {
                buffer.ShowPanel(panel.Id);
                return ValueTask.CompletedTask;
            });
        }

        if (panel.DefaultKey is { } key && !runtime.KeyBindingRegistry.Bindings.ContainsKey(KeyChord.TryParse(key, out var c) ? c.ToString() : key))
        {
            runtime.KeyBindingRegistry.Bind(key, name);
        }
    }
}
