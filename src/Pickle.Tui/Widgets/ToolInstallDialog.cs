using Pickle.Abstractions.Services;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Widgets;

/// <summary>Asks how to install a tool: for me, all users or this session only, and whether to add it to PATH.</summary>
public static class ToolInstallDialog
{
    public static readonly string[] ScopeLabels = ["Just for _me", "For _all users (administrator)", "This _session only (removed on exit)"];

    public static ToolInstallOptions? Show(IApplication app, PanelSchemes schemes, ToolPackage package, bool addToPath)
    {
        using var dialog = new Dialog { Title = "Install " + package.Name };
        var text = new Label { Text = $"'{package.Command}' comes with {package.Name} (winget {package.WingetId}).", X = 1, Y = 0 };
        var scope = new OptionSelector { X = 1, Y = 2, Labels = ScopeLabels, Value = 0 };
        var path = new CheckBox { X = 1, Y = Pos.Bottom(scope) + 1, Text = "Add to _PATH", Value = addToPath ? CheckState.Checked : CheckState.UnChecked };
        dialog.Add(text, scope, path);
        dialog.AddButton(new Button { Title = "_Cancel" });
        dialog.AddButton(new Button { Title = "_Install" });
        dialog.SetScheme(schemes.Dialog);
        scope.SetFocus();
        app.Run(dialog);
        return dialog.Result == 1
            ? new ToolInstallOptions((ToolInstallScope)Math.Clamp(scope.Value ?? 0, 0, 2), path.Value == CheckState.Checked)
            : null;
    }
}
