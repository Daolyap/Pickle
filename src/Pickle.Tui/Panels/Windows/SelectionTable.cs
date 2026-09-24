using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Windows;

/// <summary>
/// A table with a checkbox per row, shared by the winget and Windows Update panels. Marks are keyed by id, so they
/// survive refreshes and filtering; a row nobody touched uses <c>defaultMarked</c> (e.g. every upgrade starts ticked,
/// drivers don't).
/// <list type="bullet">
/// <item>Click (or Ctrl+click) a row: toggle it; it becomes the anchor.</item>
/// <item>Shift+click (Alt+click where the terminal keeps Shift+click for text selection): give every row from the
/// anchor to the clicked one the anchor's state — ticks or unticks a range.</item>
/// <item>Space: toggle the cursor row and move down. Shift+↑/↓/PgUp/PgDn: extend the anchor's state while moving.
/// Shift+Space: apply the anchor's state from the anchor to the cursor. Ctrl+A or a click on the checkbox header:
/// tick all, or untick all when everything is ticked.</item>
/// </list>
/// </summary>
internal sealed class SelectionTable<T> : TableView
    where T : class
{
    private readonly Func<T, string> _key;
    private readonly Func<T, bool> _defaultMarked;
    private readonly IReadOnlyList<(string Header, Func<T, object?> Value)> _columns;
    private readonly Dictionary<string, bool> _marks = new(StringComparer.OrdinalIgnoreCase);
    private List<T> _items = [];
    private string? _anchor;

    public SelectionTable(Func<T, string> key, Func<T, bool> defaultMarked, params (string Header, Func<T, object?> Value)[] columns)
    {
        _key = key;
        _defaultMarked = defaultMarked;
        _columns = columns;
        FullRowSelect = true;
        MultiSelect = false;
        MaxCellWidth = 32;
        Style.AlwaysShowHeaders = true;
        Table = new Source(this);
    }

    /// <summary>Raised when marks change (click, keys, <see cref="SetMarked"/>).</summary>
    public event Action? MarksChanged;

    public IReadOnlyList<T> Items => _items;

    /// <summary>The ticked rows, in display order.</summary>
    public IReadOnlyList<T> Marked => [.. _items.Where(IsMarked)];

    /// <summary>The row under the cursor.</summary>
    public T? Current => Value?.SelectedCell.Y is { } row && row >= 0 && row < _items.Count ? _items[row] : null;

    public int CursorRow => Value?.SelectedCell.Y ?? -1;

    public bool IsMarked(T item) => _marks.TryGetValue(_key(item), out var marked) ? marked : _defaultMarked(item);

    public void SetMarked(T item, bool marked)
    {
        _marks[_key(item)] = marked;
        Changed();
    }

    /// <summary>Replaces the rows. Marks (by key) and the cursor row's item are kept.</summary>
    public void SetItems(IEnumerable<T> items)
    {
        var current = Current is { } c ? _key(c) : null;
        _items = [.. items];
        Table = new Source(this);
        var index = current is null ? -1 : _items.FindIndex(i => string.Equals(_key(i), current, StringComparison.OrdinalIgnoreCase));
        if (index > 0)
        {
            MoveCursor(index);
        }

        Update();
    }

    /// <summary>Forget user choices for these keys (e.g. packages that were just removed).</summary>
    public void Forget(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            _marks.Remove(key);
        }

        Changed();
    }

    /// <summary>Click on <paramref name="row"/>; <paramref name="extend"/> is Shift/Alt+click.</summary>
    public void ClickRow(int row, bool extend)
    {
        if (row < 0 || row >= _items.Count)
        {
            return;
        }

        if (extend && AnchorRow() is { } anchor)
        {
            Paint(anchor, row, IsMarked(_items[anchor]));
        }
        else
        {
            var item = _items[row];
            _marks[_key(item)] = !IsMarked(item);
            _anchor = _key(item);
        }

        MoveCursor(row);
        Changed();
    }

    /// <summary>Ticks every row, or unticks all when all are ticked.</summary>
    public void ToggleAll()
    {
        var mark = !_items.All(IsMarked);
        foreach (var item in _items)
        {
            _marks[_key(item)] = mark;
        }

        Changed();
    }

    protected override bool OnKeyDown(Key key)
    {
        var row = CursorRow;
        if (key == Key.Space)
        {
            if (row >= 0 && row < _items.Count)
            {
                ClickRow(row, extend: false);
                MoveCursor(Math.Min(row + 1, _items.Count - 1));
            }

            return true;
        }

        if (key == Key.Space.WithShift)
        {
            if (row >= 0 && AnchorRow() is { } anchor)
            {
                Paint(anchor, row, IsMarked(_items[anchor]));
                Changed();
            }

            return true;
        }

        if (key == Key.A.WithCtrl)
        {
            ToggleAll();
            return true;
        }

        var page = Math.Max(1, Viewport.Height - 2);
        var delta = key == Key.CursorDown.WithShift ? 1
            : key == Key.CursorUp.WithShift ? -1
            : key == Key.PageDown.WithShift ? page
            : key == Key.PageUp.WithShift ? -page
            : 0;
        if (delta != 0)
        {
            ExtendBy(delta);
            return true;
        }

        return base.OnKeyDown(key);
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (!mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked) || mouse.Position is not { } position)
        {
            return base.OnMouseEvent(mouse);
        }

        var cell = ScreenToCell(position.X, position.Y, out var header);
        if (header == 0)
        {
            ToggleAll();
        }
        else if (cell is { } hit)
        {
            ClickRow(hit.Y, mouse.Flags.HasFlag(MouseFlags.Shift) || mouse.Flags.HasFlag(MouseFlags.Alt));
        }
        else
        {
            return base.OnMouseEvent(mouse);
        }

        if (CanFocus && !HasFocus)
        {
            SetFocus();
        }

        return true;
    }

    private void ExtendBy(int delta)
    {
        var row = CursorRow;
        if (row < 0 || _items.Count == 0)
        {
            return;
        }

        if (AnchorRow() is not { } anchor)
        {
            // Starting a keyboard range: it ticks, from the current row.
            anchor = row;
            _anchor = _key(_items[row]);
            _marks[_anchor] = true;
        }

        var target = Math.Clamp(row + delta, 0, _items.Count - 1);
        Paint(anchor, target, IsMarked(_items[anchor]));
        MoveCursor(target);
        Changed();
    }

    private int? AnchorRow()
    {
        if (_anchor is null)
        {
            return null;
        }

        var index = _items.FindIndex(i => string.Equals(_key(i), _anchor, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : null;
    }

    private void Paint(int from, int to, bool marked)
    {
        for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
        {
            _marks[_key(_items[i])] = marked;
        }
    }

    private void MoveCursor(int row)
    {
        if (row >= 0 && row < _items.Count)
        {
            SetSelection(0, row, extendExistingSelection: false);
            EnsureCursorIsVisible();
        }
    }

    private void Changed()
    {
        SetNeedsDraw();
        MarksChanged?.Invoke();
    }

    private sealed class Source(SelectionTable<T> owner) : ITableSource
    {
        private readonly string _checked = Glyphs.CheckStateChecked.ToString();
        private readonly string _unchecked = Glyphs.CheckStateUnChecked.ToString();

        public object this[int row, int col] =>
            col == 0
                ? owner.IsMarked(owner._items[row]) ? _checked : _unchecked
                : owner._columns[col - 1].Value(owner._items[row]) ?? string.Empty;

        public string[] ColumnNames => [" ", .. owner._columns.Select(c => c.Header)];

        public int Columns => owner._columns.Count + 1;

        public int Rows => owner._items.Count;
    }
}
