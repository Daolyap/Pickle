using System.Diagnostics;
using Pickle.Abstractions;
using Pickle.Testing;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using Terminal.Gui.ViewBase;

namespace Pickle.Tui.Tests;

/// <summary>
/// Scripted UI steps run from the Terminal.Gui main loop (Iteration event). Each step waits for its condition,
/// then acts; a step may open a nested modal (dialog) and later steps keep running inside it.
/// </summary>
internal sealed class UiScript
{
    private readonly List<(string Name, Func<IApplication, bool> When, Action<IApplication> Do)> _steps = [];
    private int _index;

    public string? Failure { get; private set; }

    public bool Finished => _index >= _steps.Count;

    public UiScript Do(string name, Action<IApplication> action) => When(name, _ => true, action);

    public UiScript When(string name, Func<IApplication, bool> condition, Action<IApplication> action)
    {
        _steps.Add((name, condition, action));
        return this;
    }

    public UiScript WaitFor(string name, Func<IApplication, bool> condition) => When(name, condition, _ => { });

    public UiScript Type(string text) => Do($"type '{text}'", app =>
    {
        foreach (var ch in text)
        {
            app.InjectKey(new Key(ch));
        }
    });

    public UiScript Press(Key key) => Do($"press {key}", app => app.InjectKey(key));

    /// <summary>Attach to an application; the script fails (and stops the app) after <paramref name="timeout"/>.</summary>
    public void Attach(IApplication app, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var clock = Stopwatch.StartNew();
        app.Iteration += (_, _) =>
        {
            if (Failure is not null)
            {
                app.TopRunnable?.RequestStop();
                return;
            }

            if (clock.Elapsed > limit)
            {
                Failure = _index < _steps.Count ? $"Timed out at step '{_steps[_index].Name}'" : "Timed out after the last step (panel did not close)";
                StopAll(app);
                return;
            }

            while (_index < _steps.Count && Failure is null)
            {
                var step = _steps[_index];
                bool ready;
                try
                {
                    ready = step.When(app);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Failure = $"Step '{step.Name}' condition threw: {ex}";
                    StopAll(app);
                    return;
                }

                if (!ready)
                {
                    return;
                }

                _index++;
                try
                {
                    step.Do(app);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Failure = $"Step '{step.Name}' threw: {ex}";
                    StopAll(app);
                    return;
                }
            }
        };
    }

    public void AssertOk() => Assert.True(Failure is null, Failure);

    private static void StopAll(IApplication app) => app.TopRunnable?.RequestStop();
}

/// <summary>Helpers to run Pickle panels headlessly (virtual time, ANSI driver, no real console).</summary>
internal static class TuiHarness
{
    public static IApplication CreateApp() => Application.Create(new VirtualTimeProvider());

    /// <summary>A started runtime with the Tui plugin loaded and its panel host wired to <paramref name="script"/>.</summary>
    public static (TestPickle Pickle, PanelHost Host) Start(UiScript? script = null, Action<PickleConfig>? configure = null)
    {
        var t = TestPickle.Create(start: true, configure: configure, plugins: [new TuiPlugin()]);
        var host = (PanelHost)t.Runtime.Services.Require<IPanelHost>();
        host.RawOutput = null;
        if (script is not null)
        {
            Use(host, script);
        }

        return (t, host);
    }

    public static void Use(PanelHost host, UiScript script, TimeSpan? timeout = null) =>
        host.ApplicationFactory = () =>
        {
            var app = CreateApp();
            script.Attach(app, timeout);
            return app;
        };

    /// <summary>Run a view directly (no PanelHost) with a script.</summary>
    public static void Run(Terminal.Gui.Views.Runnable view, UiScript script, TimeSpan? timeout = null)
    {
        using var app = CreateApp();
        app.Init();
        script.Attach(app, timeout);
        app.Run(view);
    }

    public static T Top<T>(IApplication app)
        where T : View =>
        app.TopRunnableView as T ?? throw new InvalidOperationException($"Top view is {app.TopRunnableView?.GetType().Name}, expected {typeof(T).Name}");

    public static string Screen(IApplication app)
    {
        app.LayoutAndDraw(true);
        return app.Driver?.ToString() ?? string.Empty;
    }

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickle-tui-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
