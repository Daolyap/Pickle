using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Widgets;

/// <summary>Small themed modal dialogs used by panels: text prompt and filterable pick list.</summary>
public static class PanelDialogs
{
    /// <summary>Ask for one line of text. Enter accepts, Esc cancels (returns null).</summary>
    public static string? Prompt(IApplication app, PanelSchemes schemes, string title, string label, string initial = "", int width = 60)
    {
        using var dialog = new Dialog { Title = title };
        var text = new Label { Text = label, X = 1, Y = 0 };
        var field = new TextField { X = 1, Y = 1, Width = width, Text = initial };
        dialog.Add(text, field);
        dialog.AddButton(new Button { Title = "_Cancel" });
        dialog.AddButton(new Button { Title = "_OK" });
        dialog.SetScheme(schemes.Dialog);
        field.SetScheme(schemes.Input);
        field.SetFocus();
        app.Run(dialog);
        return dialog.Result == 1 ? field.Text ?? string.Empty : null;
    }

    /// <summary>Pick one item from a fuzzy-filtered list. Enter picks, Esc cancels (returns default).</summary>
    public static T? Pick<T>(IApplication app, PanelSchemes schemes, string title, IEnumerable<T> items, Func<T, string> text, Func<T, string?>? hint = null, int width = 70, int height = 18)
        where T : notnull
    {
        using var dialog = new Dialog { Title = title, Width = width, Height = height };
        var list = new FilterableList<T>(text) { Hint = hint, Schemes = schemes, Height = Dim.Fill(1) };
        var picked = default(T);
        var done = false;
        list.ItemAccepted += (_, item) =>
        {
            picked = item;
            done = true;
            dialog.RequestStop();
        };
        list.Filter.KeyDown += (_, key) =>
        {
            if (key == Key.Esc)
            {
                dialog.RequestStop();
                key.Handled = true;
            }
        };
        dialog.Add(list);
        dialog.SetScheme(schemes.Dialog);
        list.List.SetScheme(schemes.List);
        list.Filter.SetScheme(schemes.Input);
        list.SetItems(items);
        list.Filter.SetFocus();
        app.Run(dialog);
        return done ? picked : default;
    }
}
