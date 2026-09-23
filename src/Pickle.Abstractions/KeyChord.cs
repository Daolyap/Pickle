using System.Text;

namespace Pickle.Abstractions;

/// <summary>
/// A key plus modifiers, e.g. "Ctrl+R", "Alt+G", "Shift+Enter", "F1", "Ctrl+Spacebar", "Alt+,".
/// For printable keys bound by character (like "Alt+,") <see cref="Char"/> is set and <see cref="Key"/> may be 0.
/// </summary>
public readonly record struct KeyChord(ConsoleKey Key, ConsoleModifiers Modifiers, char Char = '\0')
{
    public static KeyChord FromKeyInfo(ConsoleKeyInfo info)
    {
        var mods = info.Modifiers;
        var key = info.Key;
        var ch = info.KeyChar;

        // Ctrl+letter often arrives as a control char with Key==0 on Unix; normalize.
        if (key == 0 && ch >= '\u0001' && ch <= '\u001a')
        {
            key = ConsoleKey.A + (ch - 1);
            mods |= ConsoleModifiers.Control;
        }

        if (key is >= ConsoleKey.A and <= ConsoleKey.Z or >= ConsoleKey.D0 and <= ConsoleKey.D9
            or >= ConsoleKey.F1 and <= ConsoleKey.F24
            or ConsoleKey.Enter or ConsoleKey.Tab or ConsoleKey.Escape or ConsoleKey.Spacebar or ConsoleKey.Backspace
            or ConsoleKey.Delete or ConsoleKey.Insert or ConsoleKey.Home or ConsoleKey.End or ConsoleKey.PageUp or ConsoleKey.PageDown
            or ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.LeftArrow or ConsoleKey.RightArrow)
        {
            return new KeyChord(key, mods);
        }

        // Punctuation: bind by character, ignoring Shift (it is implied by the character).
        return new KeyChord(0, mods & ~ConsoleModifiers.Shift, char.ToLowerInvariant(ch));
    }

    public static bool TryParse(string text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var mods = (ConsoleModifiers)0;
        var parts = text.Trim();

        // Split on '+' but allow a trailing '+' key ("Ctrl++").
        var tokens = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] == '+' && current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(parts[i]);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        if (tokens.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < tokens.Count - 1; i++)
        {
            switch (tokens[i].Trim().ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= ConsoleModifiers.Control;
                    break;
                case "alt":
                case "meta":
                case "option":
                    mods |= ConsoleModifiers.Alt;
                    break;
                case "shift":
                    mods |= ConsoleModifiers.Shift;
                    break;
                default:
                    return false;
            }
        }

        var keyText = tokens[^1].Trim();
        if (keyText.Length == 1)
        {
            var c = keyText[0];
            if (char.IsLetter(c))
            {
                chord = new KeyChord(ConsoleKey.A + (char.ToUpperInvariant(c) - 'A'), mods);
                return c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
            }

            if (char.IsDigit(c))
            {
                chord = new KeyChord(ConsoleKey.D0 + (c - '0'), mods);
                return true;
            }

            chord = new KeyChord(0, mods & ~ConsoleModifiers.Shift, c);
            return true;
        }

        var aliases = new Dictionary<string, ConsoleKey>(StringComparer.OrdinalIgnoreCase)
        {
            ["space"] = ConsoleKey.Spacebar,
            ["esc"] = ConsoleKey.Escape,
            ["return"] = ConsoleKey.Enter,
            ["up"] = ConsoleKey.UpArrow,
            ["down"] = ConsoleKey.DownArrow,
            ["left"] = ConsoleKey.LeftArrow,
            ["right"] = ConsoleKey.RightArrow,
            ["del"] = ConsoleKey.Delete,
            ["ins"] = ConsoleKey.Insert,
            ["pgup"] = ConsoleKey.PageUp,
            ["pgdn"] = ConsoleKey.PageDown,
            ["bksp"] = ConsoleKey.Backspace,
        };

        if (aliases.TryGetValue(keyText, out var aliased))
        {
            chord = new KeyChord(aliased, mods);
            return true;
        }

        if (Enum.TryParse<ConsoleKey>(keyText, ignoreCase: true, out var key) && Enum.IsDefined(key))
        {
            chord = new KeyChord(key, mods);
            return true;
        }

        return false;
    }

    public static KeyChord Parse(string text) =>
        TryParse(text, out var chord) ? chord : throw new FormatException($"Invalid key chord '{text}'.");

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            sb.Append("Ctrl+");
        }

        if (Modifiers.HasFlag(ConsoleModifiers.Alt))
        {
            sb.Append("Alt+");
        }

        if (Modifiers.HasFlag(ConsoleModifiers.Shift))
        {
            sb.Append("Shift+");
        }

        if (Key == 0)
        {
            sb.Append(Char);
        }
        else if (Key is >= ConsoleKey.D0 and <= ConsoleKey.D9)
        {
            sb.Append((char)('0' + (Key - ConsoleKey.D0)));
        }
        else
        {
            sb.Append(Key.ToString());
        }

        return sb.ToString();
    }
}

/// <summary>Built-in action names. Default bindings live in Pickle.Core/Input/DefaultKeyBindings.cs.</summary>
public static class EditorActionNames
{
    public const string AcceptLine = "accept-line";
    public const string InsertNewline = "insert-newline";
    public const string CancelLine = "cancel-line";
    public const string ClearLine = "clear-line";
    public const string BackwardChar = "backward-char";
    public const string ForwardChar = "forward-char";
    public const string BackwardWord = "backward-word";
    public const string ForwardWord = "forward-word";
    public const string BeginningOfLine = "beginning-of-line";
    public const string EndOfLine = "end-of-line";
    public const string BackwardDeleteChar = "backward-delete-char";
    public const string DeleteChar = "delete-char";
    public const string BackwardKillWord = "backward-kill-word";
    public const string KillWord = "kill-word";
    public const string KillToEnd = "kill-to-end";
    public const string Undo = "undo";
    public const string Redo = "redo";
    public const string SelectBackwardChar = "select-backward-char";
    public const string SelectForwardChar = "select-forward-char";
    public const string SelectBackwardWord = "select-backward-word";
    public const string SelectForwardWord = "select-forward-word";
    public const string SelectToStart = "select-to-start";
    public const string SelectToEnd = "select-to-end";
    public const string SelectAll = "select-all";
    public const string Copy = "copy";
    public const string Cut = "cut";
    public const string Paste = "paste";
    public const string HistoryPrevious = "history-previous";
    public const string HistoryNext = "history-next";
    public const string AcceptSuggestion = "accept-suggestion";
    public const string AcceptSuggestionWord = "accept-suggestion-word";
    public const string ClearScreen = "clear-screen";
    public const string ExitIfEmpty = "exit-if-empty";
    public const string Complete = "complete";
    public const string CompletePrevious = "complete-previous";
    public const string HistorySearch = "history-search";
    public const string CommandPalette = "command-palette";
    public const string FilePickerInsert = "panel.files";
    public const string FilePickerCd = "files-cd";
    public const string OpenWizard = "wizard";
    public const string PanelGit = "panel.git";
    public const string PanelJobs = "panel.jobs";
    public const string PanelWinget = "panel.winget";
    public const string PanelUpdates = "panel.updates";
    public const string PanelScheduler = "panel.scheduler";
    public const string PanelSettings = "panel.settings";
}

/// <summary>An inline popup rendered below the input line (completion menu, history search).</summary>
public interface IEditorOverlay
{
    /// <summary>Handle a key while open. <see cref="OverlayKeyResult.NotHandled"/> lets the editor process it normally.</summary>
    OverlayKeyResult HandleKey(ConsoleKeyInfo key, IEditorBuffer buffer);

    /// <summary>Lines to draw below the input (ANSI allowed, each at most <paramref name="width"/> columns).</summary>
    IReadOnlyList<string> Render(int width, int maxRows);

    /// <summary>Called after the editor changed the buffer while the overlay is open (e.g. typing filters a menu).</summary>
    void OnBufferChanged(IEditorBuffer buffer);

    /// <summary>
    /// Optional text shown in place of the input line while open (e.g. history search shows its query).
    /// Null keeps the normal input line.
    /// </summary>
    string? InputLineOverride { get; }
}

public enum OverlayKeyResult
{
    Handled,
    NotHandled,
    Close,
}
