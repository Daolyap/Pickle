using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace Pickle.Tui.Tests.Windows;

/// <summary>
/// Runs a panel in a virtual-time Terminal.Gui app and drives it with (condition → action) steps evaluated on the UI
/// thread each iteration; the app stops after the last step (or fails after a wall-clock timeout).
/// </summary>
internal static class PanelRunner
{
    public static string Run(Window panel, params (Func<bool> Until, Action Then)[] steps)
    {
        using var app = TuiHarness.InitApp();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var index = 0;
        Exception? failure = null;
        var screen = string.Empty;
        app.Iteration += (_, _) =>
        {
            if (index >= steps.Length)
            {
                return;
            }

            try
            {
                while (index < steps.Length && steps[index].Until())
                {
                    steps[index].Then();
                    index++;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                index = steps.Length;
            }

            if (index < steps.Length && DateTime.UtcNow > deadline)
            {
                failure = new TimeoutException($"Panel step {index} never became ready.");
                index = steps.Length;
            }

            if (index >= steps.Length)
            {
                screen = app.Driver?.ToString() ?? string.Empty;
                app.RequestStop();
            }
        };

        app.Run(panel);
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }

        return screen;
    }

    public static (Func<bool>, Action) When(Func<bool> until, Action then) => (until, then);

    public static (Func<bool>, Action) Do(Action then) => (() => true, then);

    public static (Func<bool>, Action) Wait(Func<bool> until) => (until, () => { });
}
