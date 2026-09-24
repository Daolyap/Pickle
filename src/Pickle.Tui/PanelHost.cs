using Pickle.Abstractions;
using Pickle.Tui.Panels.ListPanel;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui;

/// <summary>
/// Runs full-screen panels (Terminal.Gui v2, alternate screen) modally on the REPL thread, then restores the
/// terminal for the line editor. Each Show creates a fresh IApplication (instance model) and disposes it.
/// A failing panel never takes the shell down: the exception is logged and reported as one error line after the
/// terminal has been restored. Panels can chain to another panel (<see cref="OpenNext"/>), e.g. the palette.
/// </summary>
public sealed class PanelHost : IPanelHost
{
    private const string ResetModes = "\u001b[0m\u001b[?1000l\u001b[?1002l\u001b[?1003l\u001b[?1006l\u001b[?1015l\u001b[?25h";
    private const string ForceMainScreen = "\u001b[<u\u001b[?2004l\u001b[?1049l";

    private readonly IPickleContext _pickle;
    private readonly object _gate = new();
    private (PanelDescriptor Panel, string? Argument)? _next;
    private bool _running;

    public PanelHost(IPickleContext pickle) => _pickle = pickle;

    /// <summary>Driver override (tests pass the fake driver name).</summary>
    public string? DriverName { get; set; }

    /// <summary>Creates the Terminal.Gui application for each panel (tests use a VirtualTimeProvider).</summary>
    public Func<IApplication> ApplicationFactory { get; set; } = () => Application.Create();

    /// <summary>
    /// Receives raw VT sequences written after a panel closes (cursor visible, mouse tracking off; after a failure
    /// also main screen). Null disables it. The default writes to stdout when it is a terminal.
    /// </summary>
    public Action<string>? RawOutput { get; set; } = WriteToConsole;

    /// <summary>Converts plugin list panels on demand (set by <see cref="TuiPlugin"/>).</summary>
    internal ListPanelSync? ListPanels { get; set; }

    /// <summary>The last panel failure (for tests and diagnostics).</summary>
    public Exception? LastError { get; private set; }

    public PanelResult? Show(string panelId, string? argument = null, string? currentInput = null)
    {
        var panel = Resolve(panelId);
        if (panel is null)
        {
            _pickle.Shell.WriteLine($"Unknown panel '{panelId}'.");
            return null;
        }

        return Show(panel, argument, currentInput);
    }

    public PanelResult? Show(PanelDescriptor panel, string? argument = null, string? currentInput = null)
    {
        // Inside a running pipeline (e.g. `pk git`) the runspace is busy for the panel's whole lifetime, so its
        // background work could never run: open it when the prompt is back instead.
        if (_pickle.Shell.IsBusy && !_running)
        {
            if (!_pickle.Shell.IsInteractive)
            {
                _pickle.Shell.WriteLine($"{panel.Title} needs the interactive shell.");
                return null;
            }

            _pickle.Shell.OpenPanelWhenIdle(panel, argument);
            return null;
        }

        lock (_gate)
        {
            if (_running)
            {
                // A panel asked for another panel while running: nesting Terminal.Gui applications is not supported,
                // so run it once the current one has closed.
                _next = (panel, argument);
                return null;
            }

            var result = RunOne(panel, argument, currentInput);
            while (_next is { } next)
            {
                _next = null;
                result = RunOne(next.Panel, next.Argument, currentInput);
            }

            return result;
        }
    }

    /// <summary>Open <paramref name="panelId"/> as soon as the running panel closes (its result replaces the current one).</summary>
    public bool OpenNext(string panelId, string? argument = null)
    {
        if (Resolve(panelId) is not { } panel)
        {
            return false;
        }

        _next = (panel, argument);
        return true;
    }

    private PanelDescriptor? Resolve(string panelId)
    {
        var panel = _pickle.Panels.Get(panelId);
        if (panel is null && ListPanels is { } lists)
        {
            lists.Sync();
            panel = _pickle.Panels.Get(panelId);
        }

        return panel;
    }

    private PanelResult? RunOne(PanelDescriptor panel, string? argument, string? currentInput)
    {
        if (panel.WindowsOnly && !OperatingSystem.IsWindows())
        {
            _pickle.Shell.WriteLine($"{panel.Title} is only available on Windows.");
            return null;
        }

        var context = new PanelContext { Pickle = _pickle, Argument = argument, CurrentInput = currentInput };
        Exception? failure = null;
        _running = true;
        try
        {
            using var app = ApplicationFactory();
            app.Init(DriverName);
            app.SessionBegun += (_, e) => StyleSession(e.State.Runnable);
            object? view = null;
            try
            {
                view = panel.CreateView(context);
                if (view is not IRunnable runnable)
                {
                    throw new InvalidOperationException($"Panel '{panel.Id}' did not return a Terminal.Gui Runnable.");
                }

                if (view is View root)
                {
                    DisableTerminalTitle(root);
                    PanelStyle.Apply(root, _pickle);
                }

                app.Run(runnable, ex =>
                {
                    failure ??= ex;
                    return false;
                });
            }
            finally
            {
                (view as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure ??= ex;
        }
        finally
        {
            _running = false;
            RestoreTerminal(failure is not null);
        }

        LastError = failure;
        if (failure is not null)
        {
            _next = null;
            _pickle.Log.Error("panel", $"Panel '{panel.Id}' failed", failure);
            var color = _pickle.Themes.Current.Ui.Error;
            _pickle.Shell.WriteLine($"{Ansi.Style(color)}✗ {panel.Title} failed: {FirstLine(failure.Message)}{Ansi.Reset}");
            return null;
        }

        return context.Result;
    }

    private void StyleSession(IRunnable? runnable)
    {
        if (runnable is not View view)
        {
            return;
        }

        DisableTerminalTitle(view);
        if (view is Dialog)
        {
            var error = string.Equals(view.SchemeName, "Error", StringComparison.OrdinalIgnoreCase);
            PanelStyle.ApplyDialog(view, _pickle.Themes.Current, error);
        }
    }

    private static void DisableTerminalTitle(View view)
    {
        // Terminal.Gui would otherwise leave the panel's title in the terminal's title bar after it closes.
        if (view.Border is { } border)
        {
            border.Settings &= ~BorderSettings.TerminalTitle;
        }
    }

    private void RestoreTerminal(bool failed)
    {
        try
        {
            RawOutput?.Invoke(failed ? ForceMainScreen + ResetModes : ResetModes);
        }
        catch (IOException ex)
        {
            _pickle.Log.Debug("panel", $"terminal restore failed: {ex.Message}");
        }
    }

    private static string FirstLine(string message)
    {
        var index = message.IndexOfAny(['\r', '\n']);
        return index < 0 ? message : message[..index];
    }

    // Terminal.Gui talks to the console directly while a panel runs; these sequences follow it on the same stream,
    // right after it lets go and before the line editor redraws.
    private static void WriteToConsole(string text)
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        Console.Out.Write(text);
        Console.Out.Flush();
    }
}
