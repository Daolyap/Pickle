using System.Collections;
using System.Collections.Specialized;
using System.Text;
using Pickle.Abstractions;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Pickle.Tui.Widgets;

/// <summary>
/// fzf-style picker: a filter box over a fuzzy-ranked list. Focus stays in the filter; Up/Down/PgUp/PgDn move the
/// selection, Enter raises <see cref="Accepted"/>, Space marks items when <see cref="MultiSelect"/> is on.
/// Rows show an optional category column, match highlights, and a right-aligned hint (e.g. a key chord).
/// </summary>
public class FilterableList<T> : View, IThemedWidget
    where T : notnull
{
    private const int BackgroundThreshold = 20_000;

    private readonly Func<T, string> _text;
    private readonly Label _prompt;
    private readonly Label _count;
    private readonly RowSource _source;
    private readonly List<T> _all = [];
    private readonly HashSet<T> _marked = [];
    private List<(T Item, FuzzyMatch Match)> _visible = [];
    private CancellationTokenSource? _filterCts;
    private string? _status;
    private string _appliedPattern = string.Empty;

    public FilterableList(Func<T, string> text)
    {
        _text = text;
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _prompt = new Label { Text = "❯", X = 0, Y = 0, Width = 2 };
        Filter = new TextField { X = 2, Y = 0, Width = Dim.Fill(14) };
        _count = new Label { X = Pos.AnchorEnd(13), Y = 0, Width = 13, TextAlignment = Alignment.End };
        _source = new RowSource(this);
        List = new ListView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = false,
            ShowMarks = false,
            Source = _source,
        };
        Add(_prompt, Filter, _count, List);

        Filter.ValueChanged += (_, _) => Refilter();
        Filter.KeyDown += (_, key) => key.Handled = HandleKey(key);
        List.ValueChanged += (_, _) => SelectionChanged?.Invoke(this, Selected);
        List.Accepting += (_, e) =>
        {
            Accept();
            e.Handled = true;
        };
        UpdateCount();
    }

    /// <summary>The filter text box.</summary>
    public TextField Filter { get; }

    public ListView List { get; }

    /// <summary>Right-aligned muted text per row (e.g. a chord or a size).</summary>
    public Func<T, string?>? Hint { get; set; }

    /// <summary>Left column tag per row (e.g. "Panel", "Theme").</summary>
    public Func<T, string?>? Category { get; set; }

    /// <summary>Muted text shown after the main text (not matched).</summary>
    public Func<T, string?>? Detail { get; set; }

    /// <summary>Extra searchable text for rows whose main text doesn't match (e.g. category and description).</summary>
    public Func<T, string?>? Keywords { get; set; }

    /// <summary>Optional foreground override per row (e.g. directories in the accent color).</summary>
    public Func<T, Color?>? ItemColor { get; set; }

    /// <summary>Space toggles a mark on the selected row (and moves down).</summary>
    public bool MultiSelect { get; set; }

    /// <summary>Maximum rows kept after filtering.</summary>
    public int Limit { get; set; } = 5000;

    public PanelSchemes Schemes { get; set; } = PanelStyle.For(new Theme());

    public string FilterText
    {
        get => Filter.Text ?? string.Empty;
        set => Filter.Text = value;
    }

    /// <summary>The currently selected item (default when nothing matches).</summary>
    public T? Selected => List.SelectedItem is { } i && i >= 0 && i < _visible.Count ? _visible[i].Item : default;

    /// <summary>Marked items in list order, or just the selected item when nothing is marked.</summary>
    public IReadOnlyList<T> Chosen =>
        _marked.Count > 0 ? [.. _all.Where(_marked.Contains)] : Selected is { } s ? [s] : [];

    public IReadOnlyCollection<T> Marked => _marked;

    public IReadOnlyList<T> Visible => [.. _visible.Select(v => v.Item)];

    public int TotalCount => _all.Count;

    /// <summary>Raised on Enter (or double-click) with the selected item.</summary>
    public event EventHandler<T>? Accepted;

    public event EventHandler<T?>? SelectionChanged;

    /// <summary>Status text next to the counter (e.g. "scanning…").</summary>
    public string? Status
    {
        get => _status;
        set
        {
            _status = value;
            UpdateCount();
        }
    }

    public void SetItems(IEnumerable<T> items)
    {
        _all.Clear();
        _all.AddRange(items);
        _marked.IntersectWith(_all);
        Refilter();
    }

    public void AddItems(IEnumerable<T> items)
    {
        _all.AddRange(items);
        Refilter();
    }

    public void ToggleMark(T item)
    {
        if (!_marked.Remove(item))
        {
            _marked.Add(item);
        }

        List.SetNeedsDraw();
        UpdateCount();
    }

    public void ClearMarks()
    {
        _marked.Clear();
        List.SetNeedsDraw();
        UpdateCount();
    }

    public void SelectIndex(int index)
    {
        if (_visible.Count == 0)
        {
            List.SelectedItem = null;
            return;
        }

        List.SelectedItem = Math.Clamp(index, 0, _visible.Count - 1);
        List.EnsureSelectedItemVisible();
    }

    public void Select(T item)
    {
        var index = _visible.FindIndex(v => EqualityComparer<T>.Default.Equals(v.Item, item));
        if (index >= 0)
        {
            SelectIndex(index);
        }
    }

    /// <summary>Handles navigation keys; returns true when consumed. Panels may forward keys from other views.</summary>
    public bool HandleKey(Key key)
    {
        var current = List.SelectedItem ?? -1;
        var page = Math.Max(1, List.Viewport.Height - 1);
        if (key == Key.CursorDown || key == Key.N.WithCtrl)
        {
            SelectIndex(current + 1);
        }
        else if (key == Key.CursorUp || key == Key.P.WithCtrl)
        {
            SelectIndex(Math.Max(0, current - 1));
        }
        else if (key == Key.PageDown)
        {
            SelectIndex(current + page);
        }
        else if (key == Key.PageUp)
        {
            SelectIndex(Math.Max(0, current - page));
        }
        else if (key == Key.Home.WithCtrl)
        {
            SelectIndex(0);
        }
        else if (key == Key.End.WithCtrl)
        {
            SelectIndex(_visible.Count - 1);
        }
        else if (key == Key.Enter)
        {
            Accept();
        }
        else if (key == Key.Space && MultiSelect)
        {
            if (Selected is { } item)
            {
                ToggleMark(item);
                SelectIndex(current + 1);
            }
        }
        else
        {
            return false;
        }

        return true;
    }

    /// <summary>Re-run the filter (e.g. after items changed in place).</summary>
    public void Refilter()
    {
        var pattern = FilterText;
        var items = _all.ToArray();
        var keywords = Keywords;
        _filterCts?.Cancel();
        if (items.Length < BackgroundThreshold || App is null)
        {
            ApplyResults(pattern, FuzzyFilter.Filter(items, pattern, _text, keywords, Limit));
            return;
        }

        var cts = _filterCts = new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(() => FuzzyFilter.Filter(items, pattern, _text, keywords, Limit, token), token).ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully && !token.IsCancellationRequested)
                {
                    InvokeOnUi(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            ApplyResults(pattern, t.Result);
                        }
                    });
                }
            },
            TaskScheduler.Default);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _filterCts?.Cancel();
            _filterCts?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void InvokeOnUi(Action action)
    {
        try
        {
            App?.Invoke(action);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Accept()
    {
        if (Selected is { } item)
        {
            Accepted?.Invoke(this, item);
        }
    }

    private void ApplyResults(string pattern, List<(T Item, FuzzyMatch Match)> results)
    {
        var previous = Selected;
        var patternChanged = !string.Equals(pattern, _appliedPattern, StringComparison.Ordinal);
        _appliedPattern = pattern;
        _visible = results;
        _source.RaiseReset();

        var index = 0;
        if (!patternChanged && previous is not null)
        {
            index = Math.Max(0, _visible.FindIndex(v => EqualityComparer<T>.Default.Equals(v.Item, previous)));
        }

        if (_visible.Count > 0)
        {
            List.SelectedItem = index;
            List.EnsureSelectedItemVisible();
        }
        else
        {
            List.SelectedItem = null;
        }

        List.SetNeedsDraw();
        UpdateCount();
        if (!EqualityComparer<T?>.Default.Equals(previous, Selected))
        {
            SelectionChanged?.Invoke(this, Selected);
        }
    }

    private void UpdateCount()
    {
        var text = new StringBuilder();
        if (_marked.Count > 0)
        {
            text.Append('+').Append(_marked.Count).Append(' ');
        }

        text.Append(_visible.Count).Append('/').Append(_all.Count);
        if (!string.IsNullOrEmpty(_status))
        {
            text.Insert(0, _status + " ");
        }

        _count.Text = text.ToString();
    }

    private void RenderRow(ListView listView, int item, int col, int row, int width)
    {
        var schemes = Schemes;
        if (item < 0 || item >= _visible.Count)
        {
            listView.Move(col, row);
            listView.SetAttribute(schemes.Normal);
            listView.AddStr(new string(' ', Math.Max(0, width)));
            return;
        }

        var (value, match) = _visible[item];
        var selected = listView.SelectedItem == item;
        var normal = selected ? schemes.Selected : schemes.Normal;
        var muted = selected ? schemes.MutedSelected : schemes.Muted;
        var highlight = selected ? schemes.MatchSelected : schemes.Match;
        if (!selected && ItemColor?.Invoke(value) is { } custom)
        {
            normal = new Attribute(custom, normal.Background, normal.Style);
        }

        var painter = new RowPainter(listView, col, row, width);
        if (MultiSelect)
        {
            painter.Write(_marked.Contains(value) ? "● " : "  ", _marked.Contains(value) ? highlight : muted);
        }
        else
        {
            painter.Write(selected ? "▌" : " ", selected ? highlight : muted);
        }

        if (Category?.Invoke(value) is { } category)
        {
            painter.Write(TextWidth.PadRight(TextWidth.Truncate(category, 10), 11), muted);
        }

        var hint = Hint?.Invoke(value);
        var hintWidth = string.IsNullOrEmpty(hint) ? 0 : TextWidth.VisibleWidth(hint) + 2;
        var text = _text(value);
        var available = Math.Max(0, width - painter.Column - hintWidth);
        var limit = TextWidth.VisibleWidth(text) > available ? Math.Max(0, available - 1) : available;
        var positions = match.Positions;
        var pi = 0;
        var used = 0;
        var chunk = new StringBuilder();
        var chunkIsMatch = false;
        var truncated = false;
        var i = 0;
        foreach (var element in EnumerateElements(text))
        {
            var w = TextWidth.ElementWidth(element);
            if (used + w > limit)
            {
                truncated = true;
                break;
            }

            while (pi < positions.Count && positions[pi] < i)
            {
                pi++;
            }

            var isMatch = pi < positions.Count && positions[pi] < i + element.Length;
            if (isMatch != chunkIsMatch && chunk.Length > 0)
            {
                painter.Write(chunk.ToString(), chunkIsMatch ? highlight : normal);
                chunk.Clear();
            }

            chunkIsMatch = isMatch;
            chunk.Append(element);
            used += w;
            i += element.Length;
        }

        if (chunk.Length > 0)
        {
            painter.Write(chunk.ToString(), chunkIsMatch ? highlight : normal);
        }

        if (truncated && available > 0)
        {
            painter.Write("…", muted);
        }
        else if (Detail?.Invoke(value) is { Length: > 0 } detail && available - used > 4)
        {
            painter.Write("  " + TextWidth.Truncate(detail, available - used - 2), muted);
        }

        if (hintWidth > 0)
        {
            painter.Fill(width - hintWidth + 2, normal);
            painter.Write(hint!, muted);
        }

        painter.Fill(width, normal);
    }

    private static IEnumerable<string> EnumerateElements(string text)
    {
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            yield return e.GetTextElement();
        }
    }

    private struct RowPainter(View view, int col, int row, int width)
    {
        public int Column { get; private set; }

        public void Write(string text, Attribute attribute)
        {
            var remaining = width - Column;
            if (remaining <= 0 || text.Length == 0)
            {
                return;
            }

            var fitted = TextWidth.VisibleWidth(text) > remaining ? TextWidth.Truncate(text, remaining, string.Empty) : text;
            view.Move(col + Column, row);
            view.SetAttribute(attribute);
            view.AddStr(fitted);
            Column += TextWidth.VisibleWidth(fitted);
        }

        public void Fill(int toColumn, Attribute attribute)
        {
            var count = Math.Min(toColumn, width) - Column;
            if (count > 0)
            {
                Write(new string(' ', count), attribute);
            }
        }
    }

    private sealed class RowSource(FilterableList<T> owner) : IListDataSource
    {
        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public int Count => owner._visible.Count;

        public int MaxItemLength => 0;

        public bool SuspendCollectionChangedEvent { get; set; }

        public void RaiseReset()
        {
            if (!SuspendCollectionChangedEvent)
            {
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
        }

        public bool IsMarked(int item) => item >= 0 && item < owner._visible.Count && owner._marked.Contains(owner._visible[item].Item);

        public void SetMark(int item, bool value)
        {
            if (item >= 0 && item < owner._visible.Count)
            {
                var entry = owner._visible[item].Item;
                if (value)
                {
                    owner._marked.Add(entry);
                }
                else
                {
                    owner._marked.Remove(entry);
                }
            }
        }

        public IList ToList() => owner._visible.Select(v => v.Item).ToList();

        public bool RenderMark(ListView listView, int item, int row, bool isMarked, bool markMultiple) => true;

        public void Render(ListView listView, bool selected, int item, int col, int row, int width, int viewportX) =>
            owner.RenderRow(listView, item, col, row, width);

        public void Dispose()
        {
        }
    }
}
