using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.Terminal;

namespace Pickle.Core.Modules;

/// <summary>
/// A checklist on the terminal: ↑/↓ move, Space toggles, A selects everything, N nothing, Enter accepts, Esc cancels.
/// Used by the first-start setup and <c>pk module setup</c>.
/// </summary>
public static class ModulePicker
{
    /// <summary>The ids that end up ticked, or null when cancelled or the terminal cannot take keys.</summary>
    public static IReadOnlySet<string>? Run(ITerminal terminal, UiColors ui, IReadOnlyList<ModuleStatus> modules)
    {
        if (!terminal.IsInteractive || modules.Count == 0)
        {
            return null;
        }

        var ticked = new HashSet<string>(modules.Where(m => m.Enabled).Select(m => m.Module.Id), StringComparer.OrdinalIgnoreCase);
        var current = 0;
        var width = Math.Max(30, terminal.Width - 1);
        var lines = modules.Count + 2;
        terminal.SetEditMode(true);
        try
        {
            terminal.Write(Ansi.HideCursor);
            Draw(first: true);
            while (true)
            {
                var key = terminal.ReadKey();
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow or ConsoleKey.K:
                        current = (current + modules.Count - 1) % modules.Count;
                        break;
                    case ConsoleKey.DownArrow or ConsoleKey.J:
                        current = (current + 1) % modules.Count;
                        break;
                    case ConsoleKey.Spacebar:
                        Toggle(modules[current].Module.Id);
                        break;
                    case ConsoleKey.A:
                        ticked.UnionWith(modules.Where(m => m.Module.IsSupportedHere).Select(m => m.Module.Id));
                        break;
                    case ConsoleKey.N:
                        ticked.Clear();
                        break;
                    case ConsoleKey.Enter:
                        Finish();
                        return ticked;
                    case ConsoleKey.Escape:
                        Finish();
                        return null;
                    case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                        Finish();
                        return null;
                }

                Draw(first: false);
            }
        }
        finally
        {
            terminal.Write(Ansi.ShowCursor);
            terminal.SetEditMode(false);
        }

        void Toggle(string id)
        {
            if (!ticked.Remove(id))
            {
                ticked.Add(id);
            }
        }

        void Finish() => terminal.Write("\r\n");

        void Draw(bool first)
        {
            var text = new System.Text.StringBuilder();
            if (!first)
            {
                text.Append(Ansi.CursorUp(lines - 1)).Append('\r');
            }

            text.Append(Ansi.Colorize("Optional modules", ui.Accent, bold: true))
                .Append(Ansi.Colorize("  ↑↓ move · Space toggle · A all · N none · Enter accept · Esc cancel", ui.Muted))
                .Append(Ansi.ClearToEndOfLine).Append("\r\n");
            for (var i = 0; i < modules.Count; i++)
            {
                var module = modules[i].Module;
                var supported = module.IsSupportedHere;
                var box = !supported ? "[-]" : ticked.Contains(module.Id) ? "[x]" : "[ ]";
                var label = $"{(i == current ? "›" : " ")} {box} {module.Name}";
                var detail = supported ? module.Description : $"not available on this OS";
                var room = Math.Max(0, width - TextWidth.VisibleWidth(label) - 3);
                var row = label + Ansi.Colorize(" — " + TextWidth.Truncate(detail, room), ui.Muted);
                text.Append(i == current ? Ansi.Colorize(label, ui.Accent, bold: true) + Ansi.Colorize(" — " + TextWidth.Truncate(detail, room), ui.Muted) : row)
                    .Append(Ansi.ClearToEndOfLine).Append("\r\n");
            }

            text.Append(Ansi.Colorize($"{ticked.Count} selected", ui.Muted)).Append(Ansi.ClearToEndOfLine);
            terminal.Write(text.ToString());
            terminal.Flush();
        }
    }
}
