using System.Text;
using Pickle.Abstractions;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Tests;

public class PanelHostTests
{
    private sealed class ResultPanel : PanelWindow
    {
        public ResultPanel(PanelContext context, string title, Func<PanelWindow, bool>? onOpened = null)
            : base(context, title)
        {
            OnOpenedAction = onOpened;
            Body.Add(new Label { Text = "body of " + title });
        }

        public Func<PanelWindow, bool>? OnOpenedAction { get; }

        public void Finish(PanelResult result) => Complete(result);

        public bool Chain(string id, string? argument) => OpenPanel(id, argument);

        public void Queue(Action action) => OnUi(action);
    }

    [Fact]
    public void ExceptionInCreateViewIsReportedAsOneLineAndDoesNotThrow()
    {
        var (t, host) = TuiHarness.Start(new UiScript());
        using var _ = t;
        var raw = new StringBuilder();
        host.RawOutput = s => raw.Append(s);
        var panel = new PanelDescriptor
        {
            Id = "boom",
            Title = "Boom",
            Description = "throws",
            CreateView = _ => throw new InvalidOperationException("kaput\nsecond line"),
        };

        var result = host.Show(panel);

        Assert.Null(result);
        Assert.IsType<InvalidOperationException>(host.LastError);
        var screen = t.Terminal.GetScreenText();
        Assert.Contains("Boom failed: kaput", screen);
        Assert.DoesNotContain("second line", screen);
        Assert.Contains("\u001b[?1049l", raw.ToString());
        Assert.Contains("\u001b[?25h", raw.ToString());
        Assert.Contains("\u001b[?1003l", raw.ToString());
    }

    [Fact]
    public void ExceptionWhileRunningIsCaughtAndTheShellKeepsWorking()
    {
        // Thrown from a queued UI callback (outside the test script) like a failing event handler would.
        var script = new UiScript().Do("throw on UI thread", app => Task.Run(() => app.Invoke(() => throw new InvalidOperationException("bad key"))));
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        var raw = new StringBuilder();
        host.RawOutput = s => raw.Append(s);
        var panel = new PanelDescriptor
        {
            Id = "crash",
            Title = "Crashy",
            Description = "throws while running",
            CreateView = ctx => new ResultPanel(ctx, "Crashy"),
        };

        Assert.Null(host.Show(panel));
        Assert.Equal("bad key", host.LastError?.Message);
        Assert.Contains("Crashy failed: bad key", t.Terminal.GetScreenText());
        Assert.Equal(["ok"], t.Run("'ok'"));
        Assert.DoesNotContain("\u001b[?1049h", raw.ToString());
    }

    [Fact]
    public void ReturnsThePanelResultAndRestoresTerminalModes()
    {
        var script = new UiScript()
            .Do("complete", app => TuiHarness.Top<ResultPanel>(app).Finish(new PanelResult(PanelResultKind.InsertText, "hello")));
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        var raw = new StringBuilder();
        host.RawOutput = s => raw.Append(s);
        t.Runtime.Panels.Register(new PanelDescriptor { Id = "res", Title = "Res", Description = "d", CreateView = ctx => new ResultPanel(ctx, "Res") });

        var result = host.Show("res", "arg", "current");

        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.InsertText, "hello"), result);
        Assert.Null(host.LastError);
        Assert.Contains("\u001b[?25h", raw.ToString());
        Assert.DoesNotContain("\u001b[?1049l", raw.ToString());
    }

    [Fact]
    public void OpenPanelChainsToTheNextPanelAndReturnsItsResult()
    {
        string? seenArgument = null;
        var script = new UiScript()
            .Do("chain", app => Assert.True(TuiHarness.Top<ResultPanel>(app).Chain("second", "the-arg")))
            .WaitFor("second open", app => app.TopRunnableView is ResultPanel { PanelTitle: "Second" })
            .Do("finish", app => TuiHarness.Top<ResultPanel>(app).Finish(new PanelResult(PanelResultKind.RunCommand, "done")));
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        t.Runtime.Panels.Register(new PanelDescriptor { Id = "first", Title = "First", Description = "d", CreateView = ctx => new ResultPanel(ctx, "First") });
        t.Runtime.Panels.Register(new PanelDescriptor
        {
            Id = "second",
            Title = "Second",
            Description = "d",
            CreateView = ctx =>
            {
                seenArgument = ctx.Argument;
                return new ResultPanel(ctx, "Second");
            },
        });

        var result = host.Show("first");

        script.AssertOk();
        Assert.Equal("the-arg", seenArgument);
        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, "done"), result);
    }

    [Fact]
    public void UnknownPanelWritesAMessage()
    {
        var (t, host) = TuiHarness.Start(new UiScript());
        using var _ = t;
        Assert.Null(host.Show("does-not-exist"));
        Assert.Contains("Unknown panel 'does-not-exist'", t.Terminal.GetScreenText());
    }

    [Fact]
    public void MessageBoxesAreThemedAndDoNotSetTheTerminalTitle()
    {
        Terminal.Gui.Drawing.Scheme? dialogScheme = null;
        BorderSettings? dialogBorder = null;
        var script = new UiScript()
            .Do("open message box", app => MessageBox.Query(app, "Question", "Sure?", "_Yes", "_No"))
            .WaitFor("dialog", app => app.TopRunnableView is Dialog)
            .Do("inspect", app =>
            {
                dialogScheme = app.TopRunnableView!.GetScheme();
                dialogBorder = app.TopRunnableView.Border?.Settings;
                app.InjectKey(Key.Esc);
            })
            .WaitFor("dialog closed", app => app.TopRunnableView is ResultPanel)
            .Do("close", app => app.TopRunnable!.RequestStop());
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        t.Runtime.Panels.Register(new PanelDescriptor { Id = "q", Title = "Q", Description = "d", CreateView = ctx => new ResultPanel(ctx, "Q") });

        host.Show("q");

        script.AssertOk();
        Assert.Equal(PanelStyle.For(t.Runtime).Dialog, dialogScheme);
        Assert.False(dialogBorder?.HasFlag(BorderSettings.TerminalTitle));
    }

    [Fact]
    public void OnUiBeforeTheWindowRunsIsDeliveredOnceItStarts()
    {
        var delivered = false;
        var script = new UiScript()
            .WaitFor("delivered", _ => delivered)
            .Do("close", app => app.TopRunnable!.RequestStop());
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        t.Runtime.Panels.Register(new PanelDescriptor
        {
            Id = "early",
            Title = "Early",
            Description = "d",
            CreateView = ctx =>
            {
                var panel = new ResultPanel(ctx, "Early");
                panel.Queue(() => delivered = panel.App is not null);
                return panel;
            },
        });

        host.Show("early");

        script.AssertOk();
        Assert.True(delivered);
    }

    [Fact]
    public void BuiltInPanelsAndActionsAreRegistered()
    {
        var (t, _) = TuiHarness.Start();
        using var _ = t;
        foreach (var id in new[] { "about", "palette", "files", "settings", "jobs" })
        {
            Assert.NotNull(t.Runtime.Panels.Get(id));
        }

        Assert.NotNull(t.Runtime.KeyBindings.GetAction(EditorActionNames.CommandPalette));
        Assert.NotNull(t.Runtime.KeyBindings.GetAction(EditorActionNames.FilePickerCd));
        Assert.Equal(EditorActionNames.FilePickerInsert, t.Runtime.KeyBindings.Bindings["Ctrl+T"]);
        Assert.Equal(EditorActionNames.PanelJobs, t.Runtime.KeyBindings.Bindings["Alt+J"]);
        Assert.Equal(EditorActionNames.PanelSettings, t.Runtime.KeyBindings.Bindings["Alt+,"]);
        Assert.NotNull(t.Runtime.KeyBindings.GetAction("panel.files"));
    }
}
