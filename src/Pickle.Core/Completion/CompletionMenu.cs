using System.Globalization;
using System.Text;
using Pickle.Abstractions;

namespace Pickle.Core.Completion;

/// <summary>
/// The Tab completion menu rendered below the input. It never edits the buffer until an item is accepted; typing goes
/// to the editor, and <see cref="OnBufferChanged"/> re-filters the items (fuzzy) against the word being completed.
/// </summary>
public sealed class CompletionMenu : IEditorOverlay
{
    private const int MaxNameWidth = 60;
    private const int MaxDescriptionWidth = 50;

    private readonly CompletionSet _set;
    private readonly string _originalText;
    private readonly string _originalWord;
    private readonly int _tail;
    private readonly UiColors _colors;
    private readonly int _maxRows;
    private IReadOnlyList<CompletionItem> _items;
    private string _word;
    private int _selected;
    private int _scroll;

    public CompletionMenu(CompletionSet set, string text, int cursor, UiColors colors, int maxRows = 10, bool selectLast = false)
    {
        ArgumentNullException.ThrowIfNull(set);
        _set = set;
        _originalText = text;
        _colors = colors;
        _maxRows = Math.Max(1, maxRows);
        var start = Math.Clamp(set.ReplacementIndex, 0, text.Length);
        cursor = Math.Clamp(cursor, start, text.Length);
        _tail = Math.Max(0, start + set.ReplacementLength - cursor);
        _originalWord = text[start..cursor];
        _word = _originalWord;
        _items = set.Items;
        _selected = selectLast ? _items.Count - 1 : 0;
    }

    public bool IsClosed { get; private set; }

    public IReadOnlyList<CompletionItem> Items => _items;

    public int SelectedIndex => _selected;

    public CompletionItem? Selected => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

    public string? InputLineOverride => null;

    public OverlayKeyResult HandleKey(ConsoleKeyInfo key, IEditorBuffer buffer)
    {
        if (IsClosed)
        {
            return OverlayKeyResult.Close;
        }

        var chord = KeyChord.FromKeyInfo(key);
        var plain = (chord.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0;
        var shift = chord.Modifiers.HasFlag(ConsoleModifiers.Shift);
        switch (chord.Key)
        {
            case ConsoleKey.Tab when plain && !shift:
            case ConsoleKey.DownArrow when plain:
                Move(1, wrap: true);
                return OverlayKeyResult.Handled;
            case ConsoleKey.Tab when plain && shift:
            case ConsoleKey.UpArrow when plain:
                Move(-1, wrap: true);
                return OverlayKeyResult.Handled;
            case ConsoleKey.PageDown:
                Move(VisibleRows(_maxRows), wrap: false);
                return OverlayKeyResult.Handled;
            case ConsoleKey.PageUp:
                Move(-VisibleRows(_maxRows), wrap: false);
                return OverlayKeyResult.Handled;
            case ConsoleKey.Enter when plain && !shift:
            case ConsoleKey.RightArrow when plain && !shift:
                Accept(buffer);
                return OverlayKeyResult.Close;
            case ConsoleKey.Escape:
                IsClosed = true;
                return OverlayKeyResult.Close;
            default:
                return OverlayKeyResult.NotHandled;
        }
    }

    /// <summary>Replaces the completed word with the selected item's CompletionText (does not run the line).</summary>
    public void Accept(IEditorBuffer buffer)
    {
        IsClosed = true;
        if (Selected is not { } item)
        {
            return;
        }

        var text = buffer.Text;
        var start = Math.Clamp(_set.ReplacementIndex, 0, text.Length);
        var cursor = Math.Clamp(buffer.Cursor, start, text.Length);
        var end = Math.Min(text.Length, cursor + _tail);
        buffer.Replace(string.Concat(text.AsSpan(0, start), item.CompletionText, text.AsSpan(end)), start + item.CompletionText.Length);
    }

    public void OnBufferChanged(IEditorBuffer buffer)
    {
        if (IsClosed)
        {
            return;
        }

        var text = buffer.Text;
        var start = Math.Clamp(_set.ReplacementIndex, 0, _originalText.Length);
        var cursor = buffer.Cursor;
        if (cursor < start || text.Length < start || !text.AsSpan(0, start).SequenceEqual(_originalText.AsSpan(0, start)))
        {
            Close(buffer);
            return;
        }

        var word = text[start..cursor];
        if (word.Length == 0 && _originalWord.Length > 0)
        {
            Close(buffer);
            return;
        }

        var quoted = _originalWord.Length > 0 && _originalWord[0] is '\'' or '"';
        if (!quoted && word.AsSpan().IndexOfAny(' ', '\t', '\n') >= 0)
        {
            Close(buffer);
            return;
        }

        if (word == _word)
        {
            return;
        }

        var previous = Selected;
        _word = word;
        _items = word == _originalWord
            ? _set.Items
            : [.. FuzzyMatcher.Rank(word.ToLowerInvariant(), _set.Items, i => i.CompletionText, int.MaxValue).Select(r => r.Item)];
        if (_items.Count == 0)
        {
            Close(buffer);
            return;
        }

        _selected = previous is null ? 0 : Math.Max(0, IndexOf(previous));
        _scroll = 0;
    }

    public IReadOnlyList<string> Render(int width, int maxRows)
    {
        var count = _items.Count;
        if (IsClosed || count == 0 || width < 8 || maxRows <= 0)
        {
            return [];
        }

        var rows = VisibleRows(maxRows);
        var footer = count > rows && rows < Math.Min(_maxRows, maxRows);
        EnsureVisible(rows);

        var names = new string[count];
        var descriptions = new string[count];
        var maxName = 0;
        var maxDescription = 0;
        for (var i = 0; i < count; i++)
        {
            names[i] = Sanitize(_items[i].ListText);
            descriptions[i] = Sanitize(_items[i].Description ?? string.Empty);
            maxName = Math.Max(maxName, TextWidth.VisibleWidth(names[i]));
            maxDescription = Math.Max(maxDescription, TextWidth.VisibleWidth(descriptions[i]));
        }

        // " m " marker + name + ["  " + description] + " "
        var available = width - 4;
        var nameWidth = Math.Min(Math.Min(maxName, MaxNameWidth), available);
        var descriptionWidth = Math.Min(maxDescription, MaxDescriptionWidth);
        if (descriptionWidth > 0)
        {
            var wanted = Math.Min(descriptionWidth, 12);
            if (available - nameWidth - 2 < wanted)
            {
                nameWidth = Math.Max(Math.Min(maxName, available / 2), available - 2 - wanted);
            }

            var room = available - nameWidth - 2;
            descriptionWidth = room >= 4 ? Math.Min(descriptionWidth, room) : 0;
        }

        var menuWidth = 3 + nameWidth + (descriptionWidth > 0 ? 2 + descriptionWidth : 0) + 1;
        var normal = Ansi.Style(_colors.MenuForeground, _colors.MenuBackground);
        var selectedStyle = Ansi.Style(_colors.MenuSelectedForeground, _colors.MenuSelectedBackground);
        var highlightQuery = HighlightQuery(_word);
        var lines = new List<string>(rows + 1);
        for (var i = _scroll; i < Math.Min(count, _scroll + rows); i++)
        {
            var selected = i == _selected;
            var style = selected ? selectedStyle : normal;
            var sb = new StringBuilder(menuWidth * 3);
            sb.Append(style).Append(' ');
            sb.Append(selected ? style : Ansi.Style(_colors.Accent, _colors.MenuBackground)).Append(Marker(_items[i].Kind)).Append(style).Append(' ');

            var name = TextWidth.Truncate(names[i], nameWidth);
            var positions = HighlightPositions(highlightQuery, names[i], name);
            sb.Append(FuzzyMatcher.Highlight(name, positions, selected ? null : _colors.MatchHighlight, style));
            sb.Append(' ', Math.Max(0, nameWidth - TextWidth.VisibleWidth(name)));
            if (descriptionWidth > 0)
            {
                sb.Append("  ");
                sb.Append(selected ? style : Ansi.Style(_colors.MenuDescription, _colors.MenuBackground));
                sb.Append(TextWidth.PadRight(descriptions[i], descriptionWidth)).Append(style);
            }

            sb.Append(' ').Append(Ansi.Reset);
            lines.Add(sb.ToString());
        }

        if (footer)
        {
            var indicator = $"{(_selected + 1).ToString(CultureInfo.InvariantCulture)}/{count.ToString(CultureInfo.InvariantCulture)} ";
            var text = indicator.Length >= menuWidth ? indicator[..menuWidth] : new string(' ', menuWidth - indicator.Length) + indicator;
            lines.Add(Ansi.Style(_colors.MenuDescription, _colors.MenuBackground) + text + Ansi.Reset);
        }

        return lines;
    }

    internal static string Marker(CompletionKind kind) => kind switch
    {
        CompletionKind.Command => "›",
        CompletionKind.Alias => "»",
        CompletionKind.Parameter => "-",
        CompletionKind.ParameterValue => "=",
        CompletionKind.File => "·",
        CompletionKind.Directory => "/",
        CompletionKind.Variable => "$",
        CompletionKind.Property => ".",
        CompletionKind.Method => "ƒ",
        CompletionKind.Type => "T",
        CompletionKind.Keyword => "#",
        CompletionKind.History => "↺",
        CompletionKind.Wizard => "*",
        CompletionKind.Text => "\"",
        _ => "•",
    };

    private void Close(IEditorBuffer buffer)
    {
        IsClosed = true;
        buffer.CloseOverlay();
    }

    private int VisibleRows(int maxRows)
    {
        var rows = Math.Max(1, Math.Min(_maxRows, maxRows));

        // Reserve a row for the "3/42" indicator when the list scrolls.
        return _items.Count > rows && rows > 1 ? rows - 1 : rows;
    }

    private void Move(int delta, bool wrap)
    {
        var count = _items.Count;
        if (count == 0)
        {
            return;
        }

        var next = _selected + delta;
        _selected = wrap ? ((next % count) + count) % count : Math.Clamp(next, 0, count - 1);
    }

    private void EnsureVisible(int rows)
    {
        if (_selected < _scroll)
        {
            _scroll = _selected;
        }
        else if (_selected >= _scroll + rows)
        {
            _scroll = _selected - rows + 1;
        }

        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _items.Count - rows));
    }

    private int IndexOf(CompletionItem item)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (ReferenceEquals(_items[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The part of the typed word that shows up in list text: after the last path separator, minus sigils/quotes.</summary>
    private static string HighlightQuery(string word)
    {
        var slash = word.AsSpan().LastIndexOfAny('/', '\\');
        var tail = slash >= 0 ? word[(slash + 1)..] : word;
        return tail.TrimStart('-', '$', '@', '\'', '"', '&', ' ', '[', '.').ToLowerInvariant();
    }

    private static List<int> HighlightPositions(string query, string full, string shown)
    {
        if (query.Length == 0 || FuzzyMatcher.Match(query, full) is not { } match)
        {
            return [];
        }

        var limit = shown.Length == full.Length ? shown.Length : shown.Length - 1;
        return [.. match.Positions.Where(p => p < limit)];
    }

    private static string Sanitize(string text)
    {
        var newline = text.AsSpan().IndexOfAny('\r', '\n');
        var line = (newline >= 0 ? text[..newline] : text).Trim();
        if (!line.Any(char.IsControl))
        {
            return line;
        }

        return string.Create(line.Length, line, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });
    }
}
