using Pickle.Testing;
using Pickle.Tui.Panels.NetTools;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Tests;

public class FocusTests
{
    [Fact]
    public void ContainersHoldingFocusableViewsAreOpenedButOthersAreLeftAlone()
    {
        var root = new View();
        var group = new View();
        var field = new TextField();
        var labels = new View();
        group.Add(field);
        labels.Add(new Label());
        root.Add(group, labels);

        Assert.True(FocusSync.OpenFocusPaths(root));

        Assert.True(root.CanFocus);
        Assert.True(group.CanFocus);
        Assert.False(labels.CanFocus);
    }

    [Fact]
    public void ClickingANetworkToolsTextBoxFocusesItAndTypingGoesThere()
    {
        var clicked = false;
        var script = new UiScript()
            .WaitFor("laid out", app => Ports(app).FrameToScreen().Width > 0)
            .Do("click", app =>
            {
                var box = Ports(app).FrameToScreen();
                Click(app, box.X + 5, box.Y + 1);
            })
            .WaitFor("focused", app => clicked = Ports(app).HasFocus)
            .Type("9")
            .WaitFor("typed", app => Ports(app).Text.Contains('9', StringComparison.Ordinal))
            .Press(Key.Esc);
        using var t = TestPickle.Create(start: true, plugins: [new TuiPlugin(), new NetToolsPanelPlugin()]);
        var host = (PanelHost)t.Runtime.Services.Require<Pickle.Abstractions.IPanelHost>();
        host.RawOutput = null;
        TuiHarness.Use(host, script, TimeSpan.FromSeconds(10));

        host.Show(NetToolsPanel.PanelId);

        script.AssertOk();
        Assert.True(clicked);
    }

    [Fact]
    public void ClickingAnOptionSelectsIt()
    {
        using var app = TuiHarness.InitApp();
        var window = new Runnable();
        var selector = new OptionSelector { X = 2, Y = 2, Labels = ["Machine", "User"], Orientation = Orientation.Horizontal, Value = 0 };
        window.Add(new TextField { Width = 10 }, selector);
        int? value = null;
        var script = new UiScript()
            .WaitFor("laid out", _ => selector.FrameToScreen().Width > 0)
            .Do("click", a =>
            {
                var r = selector.FrameToScreen();
                var line = TuiHarness.Screen(a).Split('\n')[r.Y];
                Click(a, line.IndexOf("User", StringComparison.Ordinal) + 1, r.Y);
            })
            .WaitFor("selected", _ => selector.Value == 1)
            .Do("read", _ => value = selector.Value)
            .Do("stop", a => a.TopRunnable?.RequestStop());
        script.Attach(app, TimeSpan.FromSeconds(10));
        app.Run(window);

        script.AssertOk();
        Assert.Equal(1, value);
    }

    [Fact]
    public void ClickingATabHeaderSwitchesToIt()
    {
        using var app = TuiHarness.InitApp();
        var window = new Runnable();
        var tabs = new Tabs { Width = Dim.Fill(), Height = Dim.Fill() };
        var first = new View { Title = "Installed", CanFocus = true };
        var second = new View { Title = "Upgrades", CanFocus = true };
        first.Add(new TextField { Width = 10 });
        second.Add(new TextField { Width = 10 });
        tabs.Add(first, second);
        window.Add(tabs);
        var script = new UiScript()
            .WaitFor("laid out", a => TuiHarness.Screen(a).Contains("Upgrades", StringComparison.Ordinal))
            .Do("click", a =>
            {
                var lines = TuiHarness.Screen(a).Split('\n');
                var y = Array.FindIndex(lines, l => l.Contains("Upgrades", StringComparison.Ordinal));
                Click(a, lines[y].IndexOf("Upgrades", StringComparison.Ordinal) + 2, y);
            })
            .WaitFor("switched", _ => ReferenceEquals(tabs.Value, second))
            .Do("stop", a => a.TopRunnable?.RequestStop());
        script.Attach(app, TimeSpan.FromSeconds(10));
        app.Run(window);

        script.AssertOk();
    }

    private static TextField Ports(IApplication app) => (TextField)TuiHarness.Top<NetToolsPanel>(app).Editor("ports")!;

    private static void Click(IApplication app, int x, int y)
    {
        var input = (ITestableInput<char>)((InputProcessorImpl<char>)app.Driver!.GetInputProcessor()).InputImpl!;
        foreach (var c in $"\u001b[<0;{x + 1};{y + 1}M\u001b[<0;{x + 1};{y + 1}m")
        {
            input.InjectInput(c);
        }
    }
}
