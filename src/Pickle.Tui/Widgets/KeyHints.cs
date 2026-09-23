using Pickle.Abstractions;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace Pickle.Tui.Widgets;

/// <summary>Helpers for showing and capturing key chords in panels.</summary>
public static class KeyHints
{
    /// <summary>Chords bound to <paramref name="actionName"/>, shortest first ("F1", "Ctrl+P").</summary>
    public static IReadOnlyList<string> ChordsFor(IKeyBindingRegistry registry, string actionName) =>
        [.. registry.Bindings
            .Where(b => string.Equals(b.Value, actionName, StringComparison.OrdinalIgnoreCase))
            .Select(b => b.Key)
            .OrderBy(c => c.Length)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Up to two chords for display, e.g. "F1 · Ctrl+P"; null when unbound.</summary>
    public static string? Describe(IKeyBindingRegistry registry, string actionName)
    {
        var chords = ChordsFor(registry, actionName);
        return chords.Count == 0 ? null : string.Join(" · ", chords.Take(2));
    }

    /// <summary>Converts a Terminal.Gui key press into a Pickle chord string ("Ctrl+R", "Alt+,", "F5"); null if unsupported.</summary>
    public static string? ToChord(Key key)
    {
        var code = key.KeyCode & ~(KeyCode.CtrlMask | KeyCode.AltMask | KeyCode.ShiftMask);
        string? name = code switch
        {
            KeyCode.Esc => "Escape",
            KeyCode.Space => "Spacebar",
            KeyCode.CursorUp => "UpArrow",
            KeyCode.CursorDown => "DownArrow",
            KeyCode.CursorLeft => "LeftArrow",
            KeyCode.CursorRight => "RightArrow",
            KeyCode.Enter or KeyCode.Tab or KeyCode.Backspace or KeyCode.Delete or KeyCode.Insert or KeyCode.Home
                or KeyCode.End or KeyCode.PageUp or KeyCode.PageDown => code.ToString(),
            >= KeyCode.A and <= KeyCode.Z => ((char)code).ToString(),
            >= (KeyCode)'a' and <= (KeyCode)'z' => char.ToUpperInvariant((char)code).ToString(),
            >= KeyCode.D0 and <= KeyCode.D9 => ((char)code).ToString(),
            _ when code.ToString() is ['F', ..] fn && fn.Length <= 3 && int.TryParse(fn[1..], out _) => fn,
            _ when (uint)code is > 32 and < 127 => ((char)code).ToString(),
            _ => null,
        };
        if (name is null)
        {
            return null;
        }

        var text = (key.IsCtrl ? "Ctrl+" : string.Empty) + (key.IsAlt ? "Alt+" : string.Empty) + (key.IsShift ? "Shift+" : string.Empty) + name;
        return KeyChord.TryParse(text, out var chord) ? chord.ToString() : null;
    }
}
