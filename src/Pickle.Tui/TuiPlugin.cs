using Pickle.Abstractions;
using Pickle.Tui.Panels.Files;
using Pickle.Tui.Panels.Jobs;
using Pickle.Tui.Panels.ListPanel;
using Pickle.Tui.Panels.Palette;
using Pickle.Tui.Panels.Settings;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui;

/// <summary>
/// Registers the panel host service and the general-purpose panels: About, the command palette (F1/Ctrl+P),
/// Files (Ctrl+T, Alt+C), Settings (Alt+,), Jobs (Alt+J), and PowerShell-plugin list panels (Register-PicklePanel).
/// </summary>
public sealed class TuiPlugin : IPicklePlugin
{
    public string Id => "pickle.tui";

    public string DisplayName => "Panels";

    public string Description => "Full-screen panels, command palette, file picker, settings and jobs.";

    public void Initialize(IPickleContext context)
    {
        var lists = new ListPanelSync(context);
        context.Services.Add<IPanelHost>(new PanelHost(context) { ListPanels = lists });
        context.Panels.Register(new PanelDescriptor
        {
            Id = "about",
            Title = "About Pickle",
            Description = "Version and credits",
            CreateView = ctx => new AboutPanel(ctx),
        });

        new PalettePanelPlugin().Initialize(context);
        new FilesPanelPlugin().Initialize(context);
        new SettingsPanelPlugin().Initialize(context);
        new JobsPanelPlugin().Initialize(context);

        lists.Sync();
        context.Hooks.Register(HookKind.Prompt, (_, _) =>
        {
            lists.Sync();
            return ValueTask.CompletedTask;
        });
    }

    private sealed class AboutPanel : PanelWindow
    {
        public AboutPanel(PanelContext context)
            : base(context, "About Pickle")
        {
            Body.Add(new Label { Text = "🥒 Pickle — a PowerShell 7 shell with superpowers.", X = 1, Y = 1 });
            Body.Add(new Label { Text = "F1 command palette · Ctrl+T files · Alt+, settings · Alt+J jobs", X = 1, Y = 3 });
            Body.Add(new Label { Text = $"Config: {context.Pickle.Paths.ConfigFile}", X = 1, Y = 4, Width = Dim.Fill(1) });
        }
    }
}
