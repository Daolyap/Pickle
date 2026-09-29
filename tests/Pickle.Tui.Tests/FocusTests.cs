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
