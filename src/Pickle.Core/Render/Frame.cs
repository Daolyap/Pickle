namespace Pickle.Core.Render;

/// <summary>
/// One terminal column. <see cref="Text"/> is what gets written (a grapheme, possibly with zero-width escape
/// sequences such as OSC marks attached); it is null for the second column of a wide character.
/// <see cref="Style"/> is the SGR sequence(s) to apply after a reset, or empty for the default style.
/// </summary>
public readonly record struct Cell(string? Text, string Style)
{
    public static readonly Cell Blank = new(" ", string.Empty);

    public bool IsBlank => Text == " " && Style.Length == 0;

    public bool IsWideTail => Text is null;
}

/// <summary>
/// A fully laid out screen region: rows of cells (hard row breaks, no terminal autowrap) and the cursor position.
/// Row 0 starts at column <see cref="Indent"/> (text before it belongs to someone else, e.g. a Read-Host prompt).
/// </summary>
public sealed class Frame
{
    public Frame(IReadOnlyList<Cell[]> rows, int cursorRow, int cursorColumn, int width, int indent = 0)
    {
        Rows = rows;
        CursorRow = cursorRow;
        CursorColumn = cursorColumn;
        Width = width;
        Indent = indent;
    }

    public IReadOnlyList<Cell[]> Rows { get; }

    public int CursorRow { get; }

    /// <summary>Column within the row (row 0 does not count <see cref="Indent"/>).</summary>
    public int CursorColumn { get; }

    public int Width { get; }

    public int Indent { get; }

    /// <summary>Plain text of each row (for tests and debugging).</summary>
    public IEnumerable<string> RowTexts => Rows.Select(r => string.Concat(r.Where(c => c.Text is not null).Select(c => c.Text)));
}
