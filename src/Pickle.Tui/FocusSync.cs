using System.Reflection;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui;

/// <summary>
/// Focus fixes applied to every panel and dialog.
/// <para>
/// Terminal.Gui 2.5 records a runnable's direct subview as the application's focused view when the runnable becomes
/// modal, not the view that really has focus. Keys follow the real focus chain, but a bracketed paste goes to the
/// recorded view (a panel's container), which drops it. <see cref="Sync"/> puts the real one back.
/// </para>
/// <para>
/// A view can only take focus (by click or Tab) when every ancestor can: a plain container left at the default
/// CanFocus = false silently locks the text boxes inside it. <see cref="OpenFocusPaths"/> opens those containers.
/// </para>
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
            OpenFocusPaths(view);
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

    /// <summary>Lets every container that holds a focusable view take focus. Returns whether <paramref name="view"/> holds one.</summary>
    public static bool OpenFocusPaths(View view)
    {
        var holdsFocusable = false;
        foreach (var child in view.SubViews)
        {
            holdsFocusable |= OpenFocusPaths(child) || child.CanFocus;
        }

        if (holdsFocusable && !view.CanFocus)
        {
            view.CanFocus = true;
        }

        return holdsFocusable;
    }
}
