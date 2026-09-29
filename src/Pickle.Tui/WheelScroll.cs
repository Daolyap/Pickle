using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Pickle.Tui;

/// <summary>
/// Mouse-wheel scrolling for every panel. Terminal.Gui binds the wheel only in its list-like views; a scrollable form
/// (a wizard's options, a settings page) ignored it, and so did a text box inside one. The wheel now scrolls the
/// nearest view under the pointer that either handles it itself or has more content than it shows, and every notch
/// moves <see cref="Step"/> rows (Terminal.Gui's lists and tables move one, which makes long lists crawl).
/// </summary>
internal static class WheelScroll
{
    public const int Step = 3;

    public static void Attach(IApplication app) => app.Mouse.MouseEvent += (_, e) => Handle(e);

    internal static void Handle(Mouse e)
    {
        if (e.Handled || e.Flags.HasFlag(MouseFlags.Ctrl))
        {
            return;
        }

        var flag = e.Flags.HasFlag(MouseFlags.WheeledDown) ? MouseFlags.WheeledDown : e.Flags.HasFlag(MouseFlags.WheeledUp) ? MouseFlags.WheeledUp : 0;
        if (flag == 0)
        {
            return;
        }

        for (var view = e.View; view is not null; view = view is AdornmentView { Adornment: { } adornment } ? adornment.Parent : view.SuperView)
        {
            if (view.MouseBindings.TryGet(flag, out var binding))
            {
                if (binding.Commands is { Length: > 0 } commands)
                {
                    for (var i = 0; i < Step; i++)
                    {
                        view.InvokeCommands(commands, binding);
                    }

                    e.Handled = true;
                }

                return;
            }

            var content = view.GetContentSize().Height;
            var height = view.Viewport.Height;
            if (content > height && height > 0)
            {
                var y = Math.Clamp(view.Viewport.Y + (flag == MouseFlags.WheeledDown ? Step : -Step), 0, content - height);
                if (y != view.Viewport.Y)
                {
                    view.Viewport = view.Viewport with { Y = y };
                }

                e.Handled = true;
                return;
            }
        }
    }
}
