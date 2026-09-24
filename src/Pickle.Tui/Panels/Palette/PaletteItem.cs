using Pickle.Abstractions;
using Pickle.Tui.Panels.Settings;
using Pickle.Tui.Widgets;

namespace Pickle.Tui.Panels.Palette;

public enum PaletteKind
{
    Panel,
    Wizard,
    Command,
    Action,
    Theme,
    Setting,
}

/// <summary>One palette entry. <see cref="Target"/> is the panel id, wizard id, command name, action name, theme or setting path.</summary>
public sealed record PaletteItem(PaletteKind Kind, string Title, string Target, string? Description = null, string? Chord = null)
{
    public string Category => Kind switch
    {
        PaletteKind.Panel => "Panel",
        PaletteKind.Wizard => "Wizard",
        PaletteKind.Command => "Command",
        PaletteKind.Action => "Action",
        PaletteKind.Theme => "Theme",
        _ => "Setting",
    };

    public string Keywords => $"{Category} {Title} {Description}";

    /// <summary>Everything the palette offers, in display order (panels first).</summary>
    public static IReadOnlyList<PaletteItem> Collect(IPickleContext pickle)
    {
        var items = new List<PaletteItem>();
        var bindings = pickle.KeyBindings;
        foreach (var panel in pickle.Panels.All)
        {
            if (panel.Id is PalettePanelPlugin.PanelId)
            {
                continue;
            }

            items.Add(new PaletteItem(PaletteKind.Panel, panel.Title, panel.Id, panel.Description, KeyHints.Describe(bindings, "panel." + panel.Id)));
        }

        foreach (var wizard in pickle.Wizards.All)
        {
            items.Add(new PaletteItem(PaletteKind.Wizard, string.IsNullOrWhiteSpace(wizard.Title) ? wizard.Id : wizard.Title, wizard.Id, wizard.Description));
        }

        foreach (var command in pickle.Commands.All)
        {
            items.Add(new PaletteItem(PaletteKind.Command, "pk " + command.Name, command.Name, $"{command.Description}  ·  {command.Usage}"));
        }

        foreach (var action in pickle.KeyBindings.Actions.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (action.Name.StartsWith("panel.", StringComparison.OrdinalIgnoreCase) || action.Name == EditorActionNames.CommandPalette)
            {
                continue;
            }

            items.Add(new PaletteItem(PaletteKind.Action, action.Name, action.Name, action.Description, KeyHints.Describe(bindings, action.Name)));
        }

        var current = pickle.Themes.Current.Name;
        foreach (var theme in pickle.Themes.Available)
        {
            var isCurrent = string.Equals(theme, current, StringComparison.OrdinalIgnoreCase);
            items.Add(new PaletteItem(PaletteKind.Theme, theme, theme, "Switch theme", isCurrent ? "current" : null));
        }

        foreach (var field in SettingsModel.Fields(pickle))
        {
            if (field.Path == "theme")
            {
                continue;
            }

            items.Add(new PaletteItem(PaletteKind.Setting, field.Path, field.Path, $"{field.Category}: {field.Label}", TextWidth.Truncate(SettingsModel.Read(pickle, field), 24)));
        }

        return items;
    }
}
