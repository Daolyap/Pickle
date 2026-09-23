using Pickle.Abstractions;
using Terminal.Gui.App;

namespace Pickle.Tui;

/// <summary>
/// Runs full-screen panels (Terminal.Gui v2, alternate screen) modally on the REPL thread, then restores the
/// terminal for the line editor. Each Show creates a fresh IApplication (instance model) and disposes it.
/// FOUNDATION VERSION — workstream W6 hardens console-state restore, theming (UiColors → schemes) and errors.
/// </summary>
public sealed class PanelHost : IPanelHost
{
    private readonly IPickleContext _pickle;
    private readonly object _gate = new();

    public PanelHost(IPickleContext pickle) => _pickle = pickle;

    /// <summary>Driver override (tests pass the fake driver name).</summary>
    public string? DriverName { get; set; }

    public PanelResult? Show(string panelId, string? argument = null, string? currentInput = null)
    {
        var panel = _pickle.Panels.Get(panelId);
        if (panel is null)
        {
            _pickle.Shell.WriteLine($"Unknown panel '{panelId}'.");
            return null;
        }

        return Show(panel, argument, currentInput);
    }

    public PanelResult? Show(PanelDescriptor panel, string? argument = null, string? currentInput = null)
    {
        if (panel.WindowsOnly && !OperatingSystem.IsWindows())
        {
            _pickle.Shell.WriteLine($"{panel.Title} is only available on Windows.");
            return null;
        }

        lock (_gate)
        {
            var context = new PanelContext { Pickle = _pickle, Argument = argument, CurrentInput = currentInput };
            using var app = Application.Create();
            app.Init(DriverName);
            var view = panel.CreateView(context);
            if (view is not IRunnable runnable)
            {
                throw new InvalidOperationException($"Panel '{panel.Id}' did not return a Terminal.Gui Runnable.");
            }

            try
            {
                app.Run(runnable);
            }
            finally
            {
                (view as IDisposable)?.Dispose();
            }

            return context.Result;
        }
    }
}
