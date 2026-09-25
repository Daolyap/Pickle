using Pickle.Abstractions;
using Pickle.Tui.Widgets;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Settings;

/// <summary>Asks for a key chord: press it (Ctrl/Alt/F-key combinations are captured) or type it ("Ctrl+Shift+K").</summary>
internal static class ChordDialog
{
    public static string? Show(IApplication app, PanelSchemes schemes)
    {
        using var dialog = new Dialog { Title = "New key binding" };
        var label = new Label { Text = "Press a key combination, or type one like Ctrl+Shift+K:", X = 1, Y = 0 };
        var field = InputBox.Boxed(new TextField { X = 1, Y = 1, Width = 40 });
        var error = new Label { X = 1, Y = Pos.Bottom(field), Width = 40, Text = string.Empty };
        dialog.Add(label, field, error);
        dialog.AddButton(new Button { Title = "_Cancel" });
        dialog.AddButton(new Button { Title = "_OK" });
        dialog.SetScheme(schemes.Dialog);
        field.SetScheme(schemes.Input);
        field.KeyDown += (_, key) =>
        {
            if (IsCapturable(key) && KeyHints.ToChord(key) is { } chord)
            {
                field.Text = chord;
                field.MoveEnd();
                key.Handled = true;
            }
        };

        while (true)
        {
            field.SetFocus();
            app.Run(dialog);
            if (dialog.Result != 1)
            {
                return null;
            }

            if (KeyChord.TryParse(field.Text ?? string.Empty, out var parsed))
            {
                return parsed.ToString();
            }

            error.Text = $"'{field.Text}' is not a valid chord.";
        }
    }

    private static bool IsCapturable(Key key)
    {
        var code = key.KeyCode & ~(KeyCode.CtrlMask | KeyCode.AltMask | KeyCode.ShiftMask);
        if (key.IsCtrl || key.IsAlt)
        {
            return true;
        }

        return code.ToString() is ['F', _, ..] name && name.Length <= 3 && int.TryParse(name[1..], out _);
    }
}
