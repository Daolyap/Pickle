using System.Text;
using Pickle.Abstractions;
using Pickle.Tui.Widgets;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Point = System.Drawing.Point;

namespace Pickle.Tui.Panels.SystemMonitoring;

/// <summary>A column of a <see cref="SortableTable{T}"/>. Numbers sort largest first on the first press.</summary>
internal sealed class TableColumn<T>
{
    public required string Header { get; init; }

    public required Func<T, string> Text { get; init; }

    /// <summary>Sort key (default: <see cref="Text"/>, case-insensitive).</summary>
    public Func<T, IComparable?>? SortKey { get; init; }

    public bool Numeric { get; init; }

    public int MinWidth { get; init; }

    public int MaxWidth { get; init; } = 60;

    /// <summary>Foreground override for a cell (e.g. a high CPU value).</summary>
    public Func<T, Color?>? Color { get; init; }
}

/// <summary>
/// A themed <see cref="TableView"/> over <typeparamref name="T"/> rows with click-to-sort headers (F6 next column,
/// Shift+F6 reverse) and optional type-to-filter: typing while the table has focus filters with the shared fuzzy
/// matcher, Backspace deletes, Esc clears. Replacing the rows keeps the selected item and its screen position.
/// </summary>
internal sealed class SortableTable<T> : View, IThemedWidget
    where T : notnull
{
    private readonly IReadOnlyList<TableColumn<T>> _columns;
    private readonly Func<T, object> _key;
    private readonly Func<T, string>? _filterText;
    private readonly Label? _filterLabel;
    private readonly Label? _countLabel;
    private readonly Source _source;
    private List<T> _all = [];
    private List<T> _rows = [];
    private string _filter = string.Empty;
    private PanelSchemes _schemes = PanelStyle.For(new Theme());

    public SortableTable(IReadOnlyList<TableColumn<T>> columns, Func<T, object> key, Func<T, string>? filterText = null)
    {
        _columns = columns;
        _key = key;
        _filterText = filterText;
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _source = new Source(this);
        Table = new TableView
        {
            X = 0,
            Y = filterText is null ? 0 : 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            FullRowSelect = true,
            CollectionNavigator = null,
            Table = _source,
        };
        Table.Style.ShowHorizontalHeaderOverline = false;
        Table.Style.ShowHorizontalBottomLine = false;
        Table.Style.ShowVerticalCellLines = false;
        Table.Style.ShowVerticalHeaderLines = false;
        Table.Style.ExpandLastColumn = true;
        Table.Style.AlwaysShowHeaders = true;
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var style = Table.Style.GetOrCreateColumnStyle(i);
            style.Alignment = column.Numeric ? Alignment.End : Alignment.Start;
            style.MinWidth = column.MinWidth;
            style.MaxWidth = column.MaxWidth;
            if (column.Color is { } color)
            {
                style.ColorGetter = args => args.RowIndex < _rows.Count && color(_rows[args.RowIndex]) is { } fg
                    ? new Scheme(_schemes.List) { Normal = new Attribute(fg, _schemes.List.Normal.Background) }
                    : null;
            }
        }

        if (filterText is not null)
        {
            _filterLabel = new Label { X = 0, Y = 0, Width = Dim.Fill(16) };
            _countLabel = new Label { X = Pos.AnchorEnd(16), Y = 0, Width = 16, TextAlignment = Alignment.End };
            Add(_filterLabel, _countLabel);
        }

        Add(Table);
        Table.KeyDown += (_, key) => key.Handled = HandleKey(key);
        Table.MouseEvent += (_, mouse) =>
        {
            if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked) && mouse.Position is { } position &&
                Table.ScreenToCell(position, out int? header) is null && header is { } column)
            {
                SortBy(column);
                mouse.Handled = true;
            }
        };
        Table.ValueChanged += (_, _) => SelectionChanged?.Invoke(this, Selected);
        UpdateFilterLine();
    }

    public TableView Table { get; }

    public PanelSchemes Schemes
    {
        get => _schemes;
        set
        {
            _schemes = value;
            Table.SetScheme(value.List);
            _filterLabel?.SetScheme(value.Base);
            _countLabel?.SetScheme(value.Base);
            SetNeedsDraw();
        }
    }

    public int SortColumn { get; private set; }

    public bool Descending { get; private set; }

    public string SortDescription => $"{_columns[SortColumn].Header} {(Descending ? "▼" : "▲")}";

    public string FilterText
    {
        get => _filter;
        set
        {
            _filter = value;
            UpdateFilterLine();
            Refresh();
        }
    }

    /// <summary>Visible rows (filtered and sorted).</summary>
    public IReadOnlyList<T> Rows => _rows;

    public int TotalCount => _all.Count;

    public int SelectedIndex => Table.Value?.SelectedCell.Y is { } y && y >= 0 && y < _rows.Count ? y : -1;

    public T? Selected => SelectedIndex is var i and >= 0 ? _rows[i] : default;

    public event EventHandler<T?>? SelectionChanged;

    public void SetItems(IEnumerable<T> items)
    {
        _all = [.. items];
        Refresh();
    }

    /// <summary>Sort by <paramref name="column"/>; the current sort column flips its direction.</summary>
    public void SortBy(int column, bool? descending = null)
    {
        if (column < 0 || column >= _columns.Count)
        {
            return;
        }

        Descending = descending ?? (column == SortColumn ? !Descending : _columns[column].Numeric);
        SortColumn = column;
        Refresh();
    }

    public void Select(Func<T, bool> predicate)
    {
        var index = _rows.FindIndex(r => predicate(r));
        if (index >= 0)
        {
            Table.Value = new TableSelection(new Point(0, index));
            Table.EnsureCursorIsVisible();
        }
    }

    internal bool HandleKey(Key key)
    {
        if (key == Key.F6)
        {
            SortBy((SortColumn + 1) % _columns.Count, _columns[(SortColumn + 1) % _columns.Count].Numeric);
            return true;
        }

        if (key == Key.F6.WithShift)
        {
            SortBy(SortColumn, !Descending);
            return true;
        }

        if (_filterText is null)
        {
            return false;
        }

        if (key == Key.Backspace && _filter.Length > 0)
        {
            FilterText = _filter[..^1];
            return true;
        }

        if (key == Key.Esc && _filter.Length > 0)
        {
            FilterText = string.Empty;
            return true;
        }

        if (!key.IsCtrl && !key.IsAlt && key.AsRune is { Value: > 0 } rune && !Rune.IsControl(rune))
        {
            FilterText = _filter + rune;
            return true;
        }

        return false;
    }

    private void Refresh()
    {
        var selected = Selected is { } s ? _key(s) : null;
        var previousIndex = SelectedIndex;
        var screenRow = previousIndex >= 0 ? previousIndex - Table.RowOffset : 0;

        IEnumerable<T> rows = _all;
        if (_filterText is not null && !string.IsNullOrWhiteSpace(_filter))
        {
            rows = rows.Where(r => FuzzyFilter.Match(_filter, _filterText(r)) is not null);
        }

        var column = _columns[SortColumn];
        var keyOf = column.SortKey ?? (r => column.Text(r));
        var comparer = Comparer<IComparable?>.Create(Compare);
        _rows = Descending ? [.. rows.OrderByDescending(keyOf, comparer)] : [.. rows.OrderBy(keyOf, comparer)];

        Table.Table = _source;
        if (_rows.Count == 0)
        {
            Table.Value = null;
        }
        else
        {
            var index = selected is null ? -1 : _rows.FindIndex(r => Equals(_key(r), selected));
            if (index < 0)
            {
                index = Math.Clamp(previousIndex, 0, _rows.Count - 1);
            }

            Table.Value = new TableSelection(new Point(0, index));
            Table.RowOffset = Math.Max(0, index - Math.Max(0, screenRow));
            Table.EnsureCursorIsVisible();
        }

        Table.Update();
        UpdateFilterLine();
    }

    private static int Compare(IComparable? a, IComparable? b) => (a, b) switch
    {
        (null, null) => 0,
        (null, _) => -1,
        (_, null) => 1,
        (string x, string y) => string.Compare(x, y, StringComparison.OrdinalIgnoreCase),
        _ => a.CompareTo(b),
    };

    private void UpdateFilterLine()
    {
        if (_filterLabel is null || _countLabel is null)
        {
            return;
        }

        _filterLabel.Text = _filter.Length == 0 ? "Filter: type to filter" : $"Filter: {_filter}▏  (Esc clears)";
        _countLabel.Text = _filter.Length == 0 ? SystemFormat.Count(_all.Count) : $"{SystemFormat.Count(_rows.Count)}/{SystemFormat.Count(_all.Count)}";
    }

    private sealed class Source(SortableTable<T> owner) : ITableSource
    {
        public string[] ColumnNames => [.. owner._columns.Select((c, i) => i == owner.SortColumn ? $"{c.Header} {(owner.Descending ? "▼" : "▲")}" : c.Header)];

        public int Columns => owner._columns.Count;

        public int Rows => owner._rows.Count;

        public object this[int row, int col] => row < owner._rows.Count ? owner._columns[col].Text(owner._rows[row]) : string.Empty;
    }
}
