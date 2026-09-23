using Pickle.Abstractions;
using Pickle.Tui.Widgets;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui;

/// <summary>
/// Common base for every Pickle panel: full-screen window, Esc closes, a status bar with key hints, theme colors
/// (following live theme changes), a busy indicator, and helpers to run background work and hand a
/// <see cref="PanelResult"/> back to the shell.
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
    private string _title;
    private readonly object _uiGate = new();
    private readonly List<Action> _pendingUi = [];
    private readonly List<(TimeSpan Interval, Func<bool> Tick)> _pendingTimers = [];
    private readonly List<object> _timers = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly EventHandler<Theme> _themeChanged;
    private string? _busyText;
    private int _busyCount;
    private bool _started;
    private bool _closed;

    public PanelWindow(PanelContext context, string title)
    {
        Context = context;
        _title = title;
        Title = $"{title}  ·  Esc to close";
        Width = Dim.Fill();
        Height = Dim.Fill();

        _statusBar = new StatusBar();
        base.Add(_statusBar);

        KeyDown += (_, key) =>
        {
            if (key == Key.Esc)
            {
                Close();
                key.Handled = true;
            }
        };

        IsRunningChanged += (_, e) =>
        {
            if (e.Value)
            {
                Started();
            }
            else
            {
                Stopped();
            }
        };

        _themeChanged = (_, _) => OnUi(Restyle);
        Pickle.Themes.ThemeChanged += _themeChanged;
        PanelStyle.Apply(this, context.Pickle);
    }

    public PanelContext Context { get; }

    public IPickleContext Pickle => Context.Pickle;

    /// <summary>Schemes and attributes for the current theme (use for custom drawing and widgets).</summary>
    public PanelSchemes Schemes => PanelStyle.For(Pickle);

    /// <summary>Cancelled when the panel closes; pass it to background work.</summary>
    public CancellationToken Lifetime => _lifetime.Token;

    /// <summary>True once the panel has stopped running.</summary>
    public bool IsClosed => _closed;

    /// <summary>The status bar with the key hints (see <see cref="AddHint"/>).</summary>
    public StatusBar Hints => _statusBar;

    /// <summary>The panel's title (shown as "title  ·  Esc to close", plus the busy indicator).</summary>
    public string PanelTitle
    {
        get => _title;
        protected set
        {
            _title = value;
            UpdateTitle();
        }
    }

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

    protected void Close()
    {
        if (!_closed)
        {
            RequestStop();
        }
    }

    /// <summary>Close this panel and open <paramref name="panelId"/> in its place (its result becomes the result).</summary>
    protected bool OpenPanel(string panelId, string? argument = null)
    {
        if (Pickle.Services.Get<IPanelHost>() is not PanelHost host || !host.OpenNext(panelId, argument))
        {
            return false;
        }

        Close();
        return true;
    }

    /// <summary>Called on the UI thread once the panel is running (App is available). Start loading here.</summary>
    protected virtual void OnOpened()
    {
    }

    /// <summary>Re-apply theme colors to the whole panel (called automatically on theme changes).</summary>
    protected virtual void Restyle()
    {
        PanelStyle.Apply(this, Pickle);
        foreach (var widget in Descendants(this).OfType<IThemedWidget>())
        {
            widget.Schemes = Schemes;
        }

        SetNeedsDraw();
    }

    /// <summary>
    /// Run <paramref name="work"/> off the UI thread with a busy indicator, then <paramref name="onDone"/> on the UI
    /// thread. Exceptions are shown in an error box. The token is cancelled when the panel closes.
    /// </summary>
    protected void RunInBackground<T>(Func<CancellationToken, Task<T>> work, Action<T> onDone, string busyText = "working…")
    {
        var token = _lifetime.Token;
        SetBusy(+1, busyText);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await work(token).ConfigureAwait(false);
                OnUi(() => onDone(result));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
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

    /// <summary>
    /// Marshal to the UI thread. Before the panel runs, actions are queued until it starts; after it closed they
    /// are dropped.
    /// </summary>
    protected void OnUi(Action action)
    {
        IApplication? app;
        lock (_uiGate)
        {
            if (_closed)
            {
                return;
            }

            if (!_started || App is null)
            {
                _pendingUi.Add(action);
                return;
            }

            app = App;
        }

        try
        {
            app.Invoke(() =>
            {
                if (!_closed)
                {
                    action();
                }
            });
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Call <paramref name="tick"/> on the UI thread every <paramref name="interval"/> while the panel runs.</summary>
    protected void Every(TimeSpan interval, Action tick)
    {
        bool Tick()
        {
            if (_closed)
            {
                return false;
            }

            tick();
            return true;
        }

        lock (_uiGate)
        {
            if (_started && App is { } app)
            {
                _timers.Add(app.AddTimeout(interval, Tick));
            }
            else
            {
                _pendingTimers.Add((interval, Tick));
            }
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

    /// <summary>Ask for a line of text; null when cancelled.</summary>
    protected string? Prompt(string title, string label, string initial = "") =>
        App is { } app ? PanelDialogs.Prompt(app, Schemes, title, label, initial) : null;

    /// <summary>Let the user pick one item from a filterable list; default when cancelled.</summary>
    protected T? Pick<T>(string title, IEnumerable<T> items, Func<T, string> text, Func<T, string?>? hint = null)
        where T : notnull =>
        App is { } app ? PanelDialogs.Pick(app, Schemes, title, items, text, hint) : default;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Pickle.Themes.ThemeChanged -= _themeChanged;
            MarkClosed();
            _lifetime.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Started()
    {
        List<Action> pending;
        lock (_uiGate)
        {
            if (_started || App is not { } app)
            {
                return;
            }

            _started = true;
            pending = [.. _pendingUi];
            _pendingUi.Clear();
            foreach (var (interval, tick) in _pendingTimers)
            {
                _timers.Add(app.AddTimeout(interval, tick));
            }

            _pendingTimers.Clear();
        }

        OnOpened();
        foreach (var action in pending)
        {
            action();
        }
    }

    private void Stopped()
    {
        var app = App;
        MarkClosed();
        if (app is not null)
        {
            foreach (var timer in _timers)
            {
                app.RemoveTimeout(timer);
            }
        }

        _timers.Clear();
    }

    private void MarkClosed()
    {
        lock (_uiGate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _pendingUi.Clear();
            _pendingTimers.Clear();
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SetBusy(int delta, string? text)
    {
        _busyCount = Math.Max(0, _busyCount + delta);
        if (text is not null)
        {
            _busyText = text;
        }

        UpdateTitle();
    }

    private void UpdateTitle() =>
        Title = _busyCount > 0 ? $"{_title}  ·  ⟳ {_busyText}  ·  Esc to close" : $"{_title}  ·  Esc to close";

    private static IEnumerable<View> Descendants(View view)
    {
        foreach (var sub in view.SubViews)
        {
            yield return sub;
            foreach (var nested in Descendants(sub))
            {
                yield return nested;
            }
        }
    }
}
