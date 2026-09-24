using Pickle.Abstractions;

namespace Pickle.Testing;

/// <summary>
/// An in-memory <see cref="IEditorBuffer"/> that records what actions and overlays do to it. <see cref="Press"/> and
/// <see cref="Type"/> mimic the line editor's overlay protocol: keys go to the open overlay first; unhandled keys do
/// basic editing (insert, Backspace, Left/Right) and then notify the overlay via <see cref="IEditorOverlay.OnBufferChanged"/>.
/// </summary>
public sealed class FakeEditorBuffer : IEditorBuffer
{
    public FakeEditorBuffer(string text = "", int? cursor = null)
    {
        Text = text;
        Cursor = Math.Clamp(cursor ?? text.Length, 0, text.Length);
    }

    public string Text { get; private set; }

    public int Cursor { get; private set; }

    public string? Suggestion { get; set; }

    public IEditorOverlay? Overlay { get; private set; }

    public List<IEditorOverlay> OpenedOverlays { get; } = [];

    public List<(string Text, int Cursor)> Replacements { get; } = [];

    public List<string> Inserts { get; } = [];

    public List<(string PanelId, string? Argument)> Panels { get; } = [];

    public int AcceptCount { get; private set; }

    public int RedrawCount { get; private set; }

    public bool Accepted => AcceptCount > 0;

    public void Insert(string text)
    {
        Text = Text.Insert(Cursor, text);
        Cursor += text.Length;
        Inserts.Add(text);
    }

    public void Replace(string text, int cursor)
    {
        Text = text;
        Cursor = Math.Clamp(cursor, 0, text.Length);
        Replacements.Add((Text, Cursor));
    }

    public void Accept() => AcceptCount++;

    public void Redraw() => RedrawCount++;

    public void OpenOverlay(IEditorOverlay overlay)
    {
        Overlay = overlay;
        OpenedOverlays.Add(overlay);
    }

    public void CloseOverlay() => Overlay = null;

    public void ShowPanel(string panelId, string? argument = null) => Panels.Add((panelId, argument));

    /// <summary>Send chords like "Tab", "Shift+Tab", "Ctrl+R", "Enter", "Escape", "Backspace".</summary>
    public FakeEditorBuffer Press(params string[] chords)
    {
        foreach (var chord in chords)
        {
            Send(ToKeyInfo(chord));
        }

        return this;
    }

    /// <summary>Type printable text key by key.</summary>
    public FakeEditorBuffer Type(string text)
    {
        foreach (var c in text)
        {
            Send(new ConsoleKeyInfo(c, CharKey(c), char.IsUpper(c), alt: false, control: false));
        }

        return this;
    }

    public OverlayKeyResult Send(ConsoleKeyInfo key)
    {
        if (Overlay is { } overlay)
        {
            var result = overlay.HandleKey(key, this);
            if (result == OverlayKeyResult.Close)
            {
                if (ReferenceEquals(Overlay, overlay))
                {
                    Overlay = null;
                }

                return result;
            }

            if (result == OverlayKeyResult.Handled)
            {
                return result;
            }
        }

        var before = (Text, Cursor);
        switch (key.Key)
        {
            case ConsoleKey.Backspace when Cursor > 0:
                Text = Text.Remove(Cursor - 1, 1);
                Cursor--;
                break;
            case ConsoleKey.LeftArrow when Cursor > 0:
                Cursor--;
                break;
            case ConsoleKey.RightArrow when Cursor < Text.Length:
                Cursor++;
                break;
            case ConsoleKey.Enter:
                Accept();
                break;
            default:
                if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar) && (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0)
                {
                    Text = Text.Insert(Cursor, key.KeyChar.ToString());
                    Cursor++;
                }

                break;
        }

        if (before != (Text, Cursor))
        {
            Overlay?.OnBufferChanged(this);
        }

        return OverlayKeyResult.NotHandled;
    }

    public static ConsoleKeyInfo ToKeyInfo(string chord)
    {
        var parsed = KeyChord.Parse(chord);
        var ctrl = parsed.Modifiers.HasFlag(ConsoleModifiers.Control);
        var ch = parsed.Key switch
        {
            ConsoleKey.Enter => '\r',
            ConsoleKey.Tab => '\t',
            ConsoleKey.Escape => '\u001b',
            ConsoleKey.Backspace => '\b',
            ConsoleKey.Spacebar => ' ',
            >= ConsoleKey.A and <= ConsoleKey.Z when ctrl => (char)(parsed.Key - ConsoleKey.A + 1),
            >= ConsoleKey.A and <= ConsoleKey.Z => (char)('a' + (parsed.Key - ConsoleKey.A)),
            0 => parsed.Char,
            _ => '\0',
        };
        var key = parsed.Key == 0 ? CharKey(parsed.Char) : parsed.Key;
        return new ConsoleKeyInfo(ch, key, parsed.Modifiers.HasFlag(ConsoleModifiers.Shift), parsed.Modifiers.HasFlag(ConsoleModifiers.Alt), ctrl);
    }

    private static ConsoleKey CharKey(char c) => c switch
    {
        >= 'a' and <= 'z' => ConsoleKey.A + (c - 'a'),
        >= 'A' and <= 'Z' => ConsoleKey.A + (c - 'A'),
        >= '0' and <= '9' => ConsoleKey.D0 + (c - '0'),
        ' ' => ConsoleKey.Spacebar,
        _ => 0,
    };
}
