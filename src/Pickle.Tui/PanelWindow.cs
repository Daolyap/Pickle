using Pickle.Abstractions;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui;

/// <summary>
/// Common base for every Pickle panel: full-screen window, Esc closes, a status bar with key hints, theme colors,
/// a busy indicator, and helpers to run background work and hand a <see cref="PanelResult"/> back to the shell.
/// <code>
/// public sealed class GitPanel : PanelWindow
/// {
///     public GitPanel(PanelContext ctx) : base(ctx, "Git") { AddHint(Key.F5, "Refresh", Refresh); ... }
/// }
/// </code>
/// </summary>
public class PanelWindow : Window
{
    private readonly StatusBar _statusBar;
    private readonly Label _busy;
    private int _busyCount;

    public PanelWindow(PanelContext context, string title)
    {
        Context = context;
        Title = $"{title}  ·  Esc to close";
        Width = Dim.Fill();
        Height = Dim.Fill();

        _busy = new Label { Text = string.Empty, X = Pos.AnchorEnd(14), Y = 0, Width = 14 };
        _statusBar = new StatusBar();
        base.Add(_busy);
        base.Add(_statusBar);

        KeyDown += (_, key) =>
        {
            if (key == Key.Esc)
            {
                Close();
                key.Handled = true;
            }
        };

        PanelStyle.Apply(this, context.Pickle);
    }

    public PanelContext Context { get; }

    public IPickleContext Pickle => Context.Pickle;

    /// <summary>The body area: add your views here (fills the window above the status bar).</summary>
    protected View Body
    {
        get
        {
            if (_body is null)
            {
                _body = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), CanFocus = true };
                base.Add(_body);
            }

            return _body;
        }
    }

    private View? _body;

    /// <summary>Add a key hint to the status bar that also triggers <paramref name="action"/>.</summary>
    protected void AddHint(Key key, string text, Action action) =>
        _statusBar.Add(new Shortcut(key, text, action));

    /// <summary>Finish with a result for the shell (insert text / run command / cd) and close.</summary>
    protected void Complete(PanelResult result)
    {
        Context.Result = result;
        Close();
    }

    protected void Close() => RequestStop();

    /// <summary>
    /// Run <paramref name="work"/> off the UI thread with a busy indicator, then <paramref name="onDone"/> on the UI
    /// thread. Exceptions are shown in an error box.
    /// </summary>
    protected void RunInBackground<T>(Func<CancellationToken, Task<T>> work, Action<T> onDone, string busyText = "working…")
    {
        SetBusy(+1, busyText);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await work(CancellationToken.None).ConfigureAwait(false);
                OnUi(() => onDone(result));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Pickle.Log.Warn("panel", $"{GetType().Name} background work failed", ex);
                OnUi(() => ShowError(ex.Message));
            }
            finally
            {
                OnUi(() => SetBusy(-1, null));
            }
        });
    }

    /// <summary>Marshal to the UI thread (no-op queueing if the app has already stopped).</summary>
    protected void OnUi(Action action)
    {
        if (App is { } app)
        {
            app.Invoke(action);
        }
    }

    protected bool Confirm(string title, string message)
    {
        if (App is not { } app)
        {
            return false;
        }

        return MessageBox.Query(app, title, message, "_Yes", "_No") == 0;
    }

    protected void ShowError(string message)
    {
        if (App is { } app)
        {
            MessageBox.ErrorQuery(app, "Error", message, "_OK");
        }
    }

    protected void ShowInfo(string title, string message)
    {
        if (App is { } app)
        {
            MessageBox.Query(app, title, message, "_OK");
        }
    }

    private void SetBusy(int delta, string? text)
    {
        _busyCount = Math.Max(0, _busyCount + delta);
        _busy.Text = _busyCount > 0 ? "⟳ " + (text ?? "working…") : string.Empty;
    }
}
