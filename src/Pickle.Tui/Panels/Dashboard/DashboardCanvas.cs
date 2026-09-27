using System.Globalization;
using System.Text;
using Pickle.Abstractions;

namespace Pickle.Tui.Panels.Dashboard;

/// <summary>What a cell is for; the panel and <c>pk dashboard --once</c> map roles to theme colors.</summary>
internal enum CellRole
{
    Normal,
    Muted,
    Accent,
    Title,
    Border,
    Success,
    Warning,
    Error,
    Info,
}

/// <summary>A grid of text elements with roles. Wide characters take two cells (the second is empty).</summary>
internal sealed class DashboardCanvas
{
    private readonly (string? Text, CellRole Role)[,] _cells;

    public DashboardCanvas(int width, int height)
    {
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        _cells = new (string?, CellRole)[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                _cells[y, x] = (" ", CellRole.Normal);
            }
        }
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Writes <paramref name="text"/> from (x, y), clipped at <paramref name="maxWidth"/> and the edge. Returns the columns used.</summary>
    public int Text(int x, int y, string text, CellRole role = CellRole.Normal, int maxWidth = int.MaxValue)
    {
        if (y < 0 || y >= Height || x >= Width)
        {
            return 0;
        }

        var limit = Math.Min(maxWidth, Width - x);
        var used = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            var width = TextWidth.ElementWidth(element);
            if (width == 0)
            {
                continue;
            }

            if (used + width > limit)
            {
                break;
            }

            if (x + used >= 0)
            {
                _cells[y, x + used] = (element, role);
                if (width == 2 && x + used + 1 < Width)
                {
                    _cells[y, x + used + 1] = (null, role);
                }
            }

            used += width;
        }

        return used;
    }

    /// <summary>Right-aligns <paramref name="text"/> so it ends at column <paramref name="right"/> (exclusive).</summary>
    public void TextRight(int right, int y, string text, CellRole role = CellRole.Normal) =>
        Text(right - TextWidth.VisibleWidth(text), y, text, role);

    /// <summary>A rounded box with the title in its top border.</summary>
    public void Box(int x, int y, int width, int height, string title)
    {
        if (width < 2 || height < 2)
        {
            return;
        }

        Text(x, y, "╭" + new string('─', width - 2) + "╮", CellRole.Border);
        for (var row = y + 1; row < y + height - 1; row++)
        {
            Text(x, row, "│", CellRole.Border);
            Text(x + width - 1, row, "│", CellRole.Border);
        }

        Text(x, y + height - 1, "╰" + new string('─', width - 2) + "╯", CellRole.Border);
        if (title.Length > 0 && width > 6)
        {
            Text(x + 2, y, " " + title + " ", CellRole.Title, width - 4);
        }
    }

    /// <summary>Consecutive cells with the same role, per line (for drawing and ANSI output).</summary>
    public IEnumerable<IReadOnlyList<(string Text, CellRole Role)>> Runs()
    {
        for (var y = 0; y < Height; y++)
        {
            var line = new List<(string, CellRole)>();
            var sb = new StringBuilder();
            var role = CellRole.Normal;
            for (var x = 0; x < Width; x++)
            {
                var (text, cellRole) = _cells[y, x];
                if (text is null)
                {
                    continue;
                }

                if (cellRole != role && sb.Length > 0)
                {
                    line.Add((sb.ToString(), role));
                    sb.Clear();
                }

                role = cellRole;
                sb.Append(text);
            }

            if (sb.Length > 0)
            {
                line.Add((sb.ToString(), role));
            }

            yield return line;
        }
    }

    /// <summary>The canvas as plain text lines, trailing spaces removed (tests).</summary>
    public IReadOnlyList<string> PlainLines() =>
        [.. Runs().Select(line => string.Concat(line.Select(r => r.Text)).TrimEnd())];

    /// <summary>The canvas as ANSI text in the theme's UI colors (<c>pk dashboard --once</c>).</summary>
    public string ToAnsi(Theme theme)
    {
        var sb = new StringBuilder();
        foreach (var line in Runs())
        {
            var end = line.Count;
            while (end > 0 && line[end - 1].Role == CellRole.Normal && line[end - 1].Text.Trim().Length == 0)
            {
                end--;
            }

            foreach (var (text, role) in line.Take(end))
            {
                sb.Append(role == CellRole.Normal ? text : Ansi.Colorize(text, Color(theme, role), bold: role == CellRole.Title));
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    public static string? Color(Theme theme, CellRole role) => role switch
    {
        CellRole.Muted => theme.Ui.Muted,
        CellRole.Accent or CellRole.Title => theme.Ui.Accent,
        CellRole.Border => theme.Ui.PanelBorder,
        CellRole.Success => theme.Ui.Success,
        CellRole.Warning => theme.Ui.Warning,
        CellRole.Error => theme.Ui.Error,
        CellRole.Info => theme.Ui.Info,
        _ => null,
    };
}
