using Pickle.Testing;
using Pickle.Tui.Panels.Wizard;
using Pickle.Wizards;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Tests;

public class WheelScrollTests
{
    [Fact]
    public void TheWheelScrollsAWizardFormEvenOverATextBox()
    {
        var tops = new List<int>();
        var script = new UiScript()
            .WaitFor("laid out", app => Form(app).Viewport.Height > 0 && Form(app).GetContentSize().Height > Form(app).Viewport.Height)
            .Do("wheel down over a field", app =>
            {
                var field = Descendants(Form(app)).First(v => v is TextField && v.FrameToScreen().Y < Form(app).FrameToScreen().Bottom - 1);
                var at = field.FrameToScreen();
                Wheel(app, at.X + 2, at.Y + 1, down: true);
                Wheel(app, at.X + 2, at.Y + 1, down: true);
            })
            .WaitFor("scrolled", app => Form(app).Viewport.Y > 0)
            .Do("record", app => tops.Add(Form(app).Viewport.Y))
            .Do("wheel up", app =>
            {
                var at = Form(app).FrameToScreen();
                Wheel(app, at.X + 3, at.Y + 3, down: false);
            })
            .WaitFor("back up", app => Form(app).Viewport.Y < tops[0])
            .Press(Key.Esc);
        using var t = TestPickle.Create(start: true, plugins: [new TuiPlugin(), new WizardsPlugin(), new WizardPanelPlugin()]);
        var host = (PanelHost)t.Runtime.Services.Require<Pickle.Abstractions.IPanelHost>();
        host.RawOutput = null;
        TuiHarness.Use(host, script, TimeSpan.FromSeconds(10));

        host.Show(WizardPanelPlugin.PanelId, "nmap");

        script.AssertOk();
        Assert.Equal(2 * WheelScroll.Step, tops[0]);
    }

    [Fact]
    public void AListScrollsByItsOwnCommandAStepPerNotchAndItsContainerStaysPut()
    {
        using var app = TuiHarness.InitApp();
        var holder = new View { Width = 10, Height = 5 };
        holder.SetContentSize(new System.Drawing.Size(10, 50));
        var list = new ListView { Width = 10, Height = 5 };
        list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(Enumerable.Range(0, 100).Select(i => "item " + i)));
        holder.Add(list);
        var window = new Runnable();
        window.Add(holder);
        var script = new UiScript()
            .WaitFor("laid out", _ => list.Viewport.Height > 0)
            .Do("wheel", _ =>
            {
                var e = new Mouse { Flags = MouseFlags.WheeledDown, View = list };
                WheelScroll.Handle(e);
                Assert.True(e.Handled);
            })
            .Do("stop", a => a.TopRunnable?.RequestStop());
        script.Attach(app);
        app.Run(window);

        script.AssertOk();
        Assert.Equal(WheelScroll.Step, list.Viewport.Y);
        Assert.Equal(0, holder.Viewport.Y);
    }

    private static IEnumerable<View> Descendants(View view) => view.SubViews.SelectMany(v => Descendants(v).Prepend(v));

    private static View Form(IApplication app) => TuiHarness.Top<WizardPanel>(app).Form!;

    private static void Wheel(IApplication app, int x, int y, bool down)
    {
        var input = (ITestableInput<char>)((InputProcessorImpl<char>)app.Driver!.GetInputProcessor()).InputImpl!;
        foreach (var c in $"\u001b[<{(down ? 65 : 64)};{x + 1};{y + 1}M")
        {
            input.InjectInput(c);
        }
    }
}
