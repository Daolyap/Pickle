using Pickle.Abstractions;
using Terminal.Gui.Views;

namespace Pickle.Tui;

/// <summary>
/// Registers the panel host service and the general-purpose panels.
/// FOUNDATION PLACEHOLDER — workstream W6 adds the command palette, Files, Settings and Jobs panels, and
/// the ListPanel used for PowerShell-plugin panels (Register-PicklePanel).
/// </summary>
public sealed class TuiPlugin : IPicklePlugin
{
    public string Id => "pickle.tui";

    public string DisplayName => "Panels";

    public string Description => "Full-screen panels, command palette, file picker, settings and jobs.";

    public void Initialize(IPickleContext context)
    {
        context.Services.Add<IPanelHost>(new PanelHost(context));
        context.Panels.Register(new PanelDescriptor
        {
            Id = "about",
            Title = "About Pickle",
            Description = "Version and credits",
            CreateView = ctx =>
            {
                var window = new Window { Title = "About Pickle (Esc to close)" };
                window.Add(new Label { Text = "🥒 Pickle — a PowerShell 7 shell with superpowers.", X = 1, Y = 1 });
                window.KeyDown += (_, key) =>
                {
                    if (key == Terminal.Gui.Input.Key.Esc)
                    {
                        window.RequestStop();
                        key.Handled = true;
                    }
                };
                return window;
            },
        });
    }
}
