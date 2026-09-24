using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Winget;

internal enum WingetTableKind
{
    List,
    Upgrade,
    Search,
}

internal sealed record WingetColumn(string Header, int Start);

internal sealed record WingetTable(IReadOnlyList<WingetColumn> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// Parsers for winget.exe text output (the fallback when Microsoft.WinGet.Client isn't installed). Tables are sliced by
/// terminal display columns taken from the header row, which copes with truncated names, East Asian wide characters
/// and spinner/progress noise before the header.
/// </summary>
internal static partial class WingetCliParser
{
    private enum Role
    {
        Unknown,
        Name,
        Id,
        Version,
        Available,
        Source,
        Match,
    }

    [GeneratedRegex(@"^\S+\s+(?<name>.+?)\s+\[(?<id>[^\[\]\s]+)\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FoundRegex();

    [GeneratedRegex(@"^(?<key>[^\s:][^:]{0,40}):(?:\s+(?<value>.*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueRegex();

    [GeneratedRegex(@"(?<p>\d{1,3}(?:[.,]\d+)?)\s?%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"(?<a>\d+(?:[.,]\d+)?)\s*(?<au>[KMGT]?B)\s*/\s*(?<b>\d+(?:[.,]\d+)?)\s*(?<bu>[KMGT]?B)", RegexOptions.CultureInvariant)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9.\-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex SourceNameRegex();

    /// <summary>Normalizes raw output into the lines a terminal would finally show (CR overwrites, backspaces, ANSI removed).</summary>
    public static IReadOnlyList<string> CleanLines(string raw)
    {
        var text = TextWidth.StripAnsi(raw);
        var lines = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var cr = line.LastIndexOf('\r');
            if (cr >= 0)
            {
                line = line[(cr + 1)..];
            }

            if (line.Contains('\b', StringComparison.Ordinal))
            {
                line = ApplyBackspaces(line);
            }

            lines.Add(line.TrimEnd());
        }

        return lines;
    }

    public static IReadOnlyList<WingetTable> ParseTables(string raw) => ParseTables(CleanLines(raw));

    public static IReadOnlyList<WingetTable> ParseTables(IReadOnlyList<string> lines)
    {
        var tables = new List<WingetTable>();
        var i = 0;
        while (i < lines.Count - 1)
        {
            if (!IsHeader(lines, i))
            {
                i++;
                continue;
            }

            var columns = ParseHeader(lines[i]);
            var rows = new List<IReadOnlyList<string>>();
            i += 2;
            while (i < lines.Count && lines[i].Length > 0 && !IsHeader(lines, i))
            {
                var cells = SliceRow(lines[i], columns);
                if (cells is null)
                {
                    break;
                }

                rows.Add(cells);
                i++;
            }

            tables.Add(new WingetTable(columns, rows));
        }

        return tables;
    }

    public static IReadOnlyList<WingetPackage> ParsePackages(string raw, WingetTableKind kind)
    {
        var packages = new List<WingetPackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in ParseTables(raw))
        {
            var roles = AssignRoles(table, kind);
            var idIndex = Array.IndexOf(roles, Role.Id);
            if (idIndex < 0)
            {
                continue;
            }

            foreach (var row in table.Rows)
            {
                string? Cell(Role role)
                {
                    var index = Array.IndexOf(roles, role);
                    return index >= 0 && index < row.Count && row[index].Length > 0 ? row[index] : null;
                }

                var id = Cell(Role.Id);
                if (id is null || id.Any(char.IsWhiteSpace))
                {
                    continue;
                }

                var name = Cell(Role.Name) ?? id;
                var version = Cell(Role.Version);
                var package = kind == WingetTableKind.Search
                    ? new WingetPackage(id, name, null, version, Cell(Role.Source))
                    : new WingetPackage(id, name, version, Cell(Role.Available), Cell(Role.Source));

                if (seen.Add(id + "\u0000" + package.InstalledVersion))
                {
                    packages.Add(package);
                }
            }
        }

        return packages;
    }

    /// <summary>Parses <c>winget show --id X --exact</c>. Returns null when no "Found name [id]" line is present.</summary>
    public static WingetPackageDetails? ParseShow(string raw, IReadOnlyList<string>? versions = null)
    {
        var lines = CleanLines(raw);
        var start = -1;
        string? name = null;
        string? id = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var match = FoundRegex().Match(lines[i]);
            if (match.Success)
            {
                name = match.Groups["name"].Value.Trim();
                id = match.Groups["id"].Value;
                start = i + 1;
                break;
            }
        }

        if (start < 0 || name is null || id is null)
        {
            return null;
        }

        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;
        for (var i = start; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
            {
                var kv = KeyValueRegex().Match(line);
                if (kv.Success)
                {
                    var key = kv.Groups["key"].Value.Trim();
                    current = [];
                    values.TryAdd(key, current);
                    var value = kv.Groups["value"].Value.Trim();
                    if (value.Length > 0)
                    {
                        current.Add(value);
                    }

                    continue;
                }

                current = null;
                continue;
            }

            current?.Add(line.Trim());
        }

        string? Value(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (values.TryGetValue(key, out var list) && list.Count > 0)
                {
                    return string.Join('\n', list);
                }
            }

            return null;
        }

        var latest = Value("Version");
        var available = versions is { Count: > 0 } ? versions : latest is null ? [] : [latest];
        return new WingetPackageDetails(
            id,
            name,
            Value("Publisher", "Author"),
            Value("Description", "Short Description"),
            Value("Homepage", "Publisher Url"),
            Value("License"),
            latest ?? available.FirstOrDefault(),
            available,
            Value("Release Notes", "Release Notes Url"));
    }

    /// <summary>Parses <c>winget show --id X --exact --versions</c> (a single-column table).</summary>
    public static IReadOnlyList<string> ParseVersions(string raw)
    {
        foreach (var table in ParseTables(raw))
        {
            if (table.Columns.Count >= 1)
            {
                return [.. table.Rows.Select(r => r[0]).Where(v => v.Length > 0)];
            }
        }

        return [];
    }

    /// <summary>Parses <c>winget source export</c> (one JSON object per line), falling back to the <c>winget source list</c> table.</summary>
    public static IReadOnlyList<WingetSource> ParseSources(string raw)
    {
        var sources = new List<WingetSource>();
        foreach (var line in CleanLines(raw))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                string Get(string name) =>
                    root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;
                var sourceName = Get("Name");
                if (sourceName.Length > 0)
                {
                    sources.Add(new WingetSource(sourceName, Get("Arg"), Get("Type")));
                }
            }
            catch (JsonException)
            {
            }
        }

        if (sources.Count > 0)
        {
            return sources;
        }

        foreach (var table in ParseTables(raw))
        {
            foreach (var row in table.Rows)
            {
                if (row.Count >= 2 && row[0].Length > 0)
                {
                    sources.Add(new WingetSource(row[0], row[1], string.Empty));
                }
            }
        }

        return sources;
    }

    /// <summary>Percent from a progress bar segment ("██▒▒ 45%" or "12.0 MB / 65.6 MB").</summary>
    public static double? ParsePercent(string segment)
    {
        var size = SizeRegex().Match(segment);
        if (size.Success)
        {
            var done = ToBytes(size.Groups["a"].Value, size.Groups["au"].Value);
            var total = ToBytes(size.Groups["b"].Value, size.Groups["bu"].Value);
            if (total > 0)
            {
                return Math.Clamp(Math.Round(done / total * 100, 1), 0, 100);
            }
        }

        var percent = PercentRegex().Match(segment);
        if (percent.Success && double.TryParse(percent.Groups["p"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
        {
            return Math.Clamp(p, 0, 100);
        }

        return null;
    }

    /// <summary>The last line of output that reads like a message (not a progress bar or spinner).</summary>
    public static string? LastMessage(string raw)
    {
        foreach (var line in CleanLines(raw).Reverse())
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 3 || trimmed.Contains('█') || trimmed.Contains('▒') || !trimmed.Any(char.IsLetter))
            {
                continue;
            }

            return trimmed;
        }

        return null;
    }

    private static bool IsHeader(IReadOnlyList<string> lines, int index) =>
        index + 1 < lines.Count && lines[index].Trim().Length > 0 && IsDashes(lines[index + 1]) && !IsDashes(lines[index]);

    private static bool IsDashes(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 3 && trimmed.All(c => c is '-' or '─');
    }

    private static List<WingetColumn> ParseHeader(string header)
    {
        var columns = new List<WingetColumn>();
        var col = 0;
        var previousSpace = true;
        StringBuilder? token = null;
        var tokenStart = 0;
        foreach (var (element, width) in Elements(header))
        {
            var isSpace = element == " ";
            if (!isSpace && previousSpace)
            {
                if (token is not null)
                {
                    columns.Add(new WingetColumn(token.ToString(), tokenStart));
                }

                token = new StringBuilder();
                tokenStart = col;
            }

            if (!isSpace)
            {
                token!.Append(element);
            }

            previousSpace = isSpace;
            col += width;
        }

        if (token is not null)
        {
            columns.Add(new WingetColumn(token.ToString(), tokenStart));
        }

        return columns;
    }

    /// <summary>Returns null when the line doesn't line up with the header (a footer or the next section).</summary>
    private static string[]? SliceRow(string line, IReadOnlyList<WingetColumn> columns) =>
        SliceByDisplayWidth(line, columns) ?? SliceByCharIndex(line, columns);

    private static string[]? SliceByDisplayWidth(string line, IReadOnlyList<WingetColumn> columns)
    {
        var cells = new StringBuilder[columns.Count];
        for (var c = 0; c < cells.Length; c++)
        {
            cells[c] = new StringBuilder();
        }

        var col = 0;
        var previous = " ";
        var columnIndex = 0;
        foreach (var (element, width) in Elements(line))
        {
            while (columnIndex + 1 < columns.Count && col >= columns[columnIndex + 1].Start)
            {
                if (col != columns[columnIndex + 1].Start || previous != " ")
                {
                    return null;
                }

                columnIndex++;
            }

            if (columnIndex + 1 < columns.Count && col < columns[columnIndex + 1].Start && col + width > columns[columnIndex + 1].Start)
            {
                return null;
            }

            cells[columnIndex].Append(element);
            previous = element;
            col += width;
        }

        return [.. cells.Select(c => c.ToString().Trim())];
    }

    private static string[]? SliceByCharIndex(string line, IReadOnlyList<WingetColumn> columns)
    {
        var cells = new string[columns.Count];
        for (var c = 0; c < columns.Count; c++)
        {
            var start = columns[c].Start;
            if (start >= line.Length)
            {
                cells[c] = string.Empty;
                continue;
            }

            if (start > 0 && line[start - 1] != ' ')
            {
                return null;
            }

            var end = c + 1 < columns.Count ? Math.Min(columns[c + 1].Start, line.Length) : line.Length;
            cells[c] = line[start..end].Trim();
        }

        return cells;
    }

    private static IEnumerable<(string Element, int Width)> Elements(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            yield return (element == "\t" ? " " : element, element == "\t" ? 1 : TextWidth.ElementWidth(element));
        }
    }

    private static Role[] AssignRoles(WingetTable table, WingetTableKind kind)
    {
        var roles = table.Columns.Select(c => RoleOf(c.Header)).ToArray();
        Role[] positional = [Role.Name, Role.Id, Role.Version];
        for (var i = 0; i < roles.Length; i++)
        {
            if (roles[i] != Role.Unknown)
            {
                continue;
            }

            if (i < positional.Length && !roles.Contains(positional[i]))
            {
                roles[i] = positional[i];
            }
            else if (i == roles.Length - 1 && !roles.Contains(Role.Source) && LooksLikeSources(table, i))
            {
                roles[i] = Role.Source;
            }
            else
            {
                var fallback = kind == WingetTableKind.Search ? Role.Match : Role.Available;
                roles[i] = roles.Contains(fallback) ? Role.Unknown : fallback;
            }
        }

        return roles;
    }

    private static bool LooksLikeSources(WingetTable table, int column)
    {
        var values = table.Rows.Select(r => column < r.Count ? r[column] : string.Empty).Where(v => v.Length > 0).ToList();
        return values.Count > 0 && values.All(v => SourceNameRegex().IsMatch(v));
    }

    private static Role RoleOf(string header) => header.Trim().ToLowerInvariant() switch
    {
        "name" or "nom" or "nombre" or "nome" or "名称" or "名前" or "이름" => Role.Name,
        "id" or "kennung" or "identificador" => Role.Id,
        "version" or "versión" or "versione" or "versão" or "版本" or "バージョン" or "버전" => Role.Version,
        "available" or "verfügbar" or "disponible" or "disponibile" or "disponível" or "可用" or "利用可能" => Role.Available,
        "source" or "quelle" or "origen" or "origine" or "fonte" or "源" or "ソース" or "원본" => Role.Source,
        "match" or "übereinstimmung" or "correspondance" or "coincidencia" => Role.Match,
        _ => Role.Unknown,
    };

    private static double ToBytes(string number, string unit)
    {
        if (!double.TryParse(number.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return 0;
        }

        return unit switch
        {
            "KB" => value * 1024,
            "MB" => value * 1024 * 1024,
            "GB" => value * 1024 * 1024 * 1024,
            "TB" => value * 1024 * 1024 * 1024 * 1024,
            _ => value,
        };
    }

    private static string ApplyBackspaces(string line)
    {
        var sb = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (c == '\b')
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

/// <summary>Turns winget.exe's streamed output segments (split on CR/LF) into coarse <see cref="WingetProgress"/> updates.</summary>
internal sealed class WingetProgressTracker(IProgress<WingetProgress>? progress)
{
    private string _stage = "Starting";
    private double? _percent;

    public void Feed(string segment)
    {
        if (progress is null)
        {
            return;
        }

        var text = TextWidth.StripAnsi(segment).Trim();
        if (text.Length == 0)
        {
            return;
        }

        var stage = StageOf(text);
        if (stage is not null && stage != _stage)
        {
            _stage = stage;
            _percent = stage is "Installed" or "Uninstalled" ? 100 : null;
            progress.Report(new WingetProgress(_stage, _percent, text));
            return;
        }

        if (text.Contains('█') || text.Contains('▒') || text.EndsWith('%'))
        {
            var percent = WingetCliParser.ParsePercent(text);
            if (percent is { } p && (_percent is null || Math.Abs(p - _percent.Value) >= 1))
            {
                _percent = p;
                progress.Report(new WingetProgress(_stage, p));
            }
        }
    }

    private static string? StageOf(string text)
    {
        if (text.StartsWith("Found ", StringComparison.Ordinal))
        {
            return "Resolving";
        }

        if (text.StartsWith("Downloading", StringComparison.Ordinal))
        {
            return "Downloading";
        }

        if (text.StartsWith("Successfully verified", StringComparison.Ordinal))
        {
            return "Verified";
        }

        if (text.StartsWith("Starting package install", StringComparison.Ordinal))
        {
            return "Installing";
        }

        if (text.StartsWith("Starting package uninstall", StringComparison.Ordinal))
        {
            return "Uninstalling";
        }

        if (text.StartsWith("Successfully installed", StringComparison.Ordinal))
        {
            return "Installed";
        }

        if (text.StartsWith("Successfully uninstalled", StringComparison.Ordinal))
        {
            return "Uninstalled";
        }

        return null;
    }
}
