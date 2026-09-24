using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace Pickle.Tui.Tests;

public class PanelLifecycleTests
{
    /// <summary>Spike (c): creating, running and disposing Terminal.Gui applications repeatedly must be clean.</summary>
    [Fact]
    public void PanelsCanOpenAndCloseRepeatedly()
    {
        for (var i = 0; i < 10; i++)
        {
            using var app = TuiHarness.InitApp();
            app.StopAfterFirstIteration = true;
            using var window = new Window { Title = $"panel {i}" };
            window.Add(new Label { Text = "hello" });
            app.Run(window);
            Assert.False(window.IsRunning);
        }
    }
}
