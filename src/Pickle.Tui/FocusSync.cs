using System.Reflection;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui;

/// <summary>
/// Terminal.Gui 2.5 records a runnable's direct subview as the application's focused view when the runnable becomes
/// modal, not the view that really has focus. Keys follow the real focus chain, but a bracketed paste goes to the
/// recorded view (a panel's container), which drops it. This puts the real one back.
/// </summary>
internal static class FocusSync
{
    private static readonly MethodInfo? SetFocused =
        typeof(ApplicationNavigation).GetMethod("SetFocused", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(View)]);

    internal static bool Available => SetFocused is not null;

    public static void Track(IRunnable? runnable)
    {
        if (runnable is Runnable view)
        {
            view.IsModalChanged += (_, e) =>
            {
                if (e.Value)
                {
                    Sync(view);
                }
            };
        }
    }

    public static void Sync(View top)
    {
        if (top.App?.Navigation is { } navigation && top.MostFocused is { } most && navigation.GetFocused() != most)
        {
            SetFocused?.Invoke(navigation, [most]);
        }
    }
}
