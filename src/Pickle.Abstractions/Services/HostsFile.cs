using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Pickle.Abstractions.Services;

/// <summary>One hosts entry: <c>10.0.0.5  web web.lan  # comment</c>. A commented-out entry (<c># 10.0.0.5 web</c>) is a disabled one.</summary>
public sealed record HostsEntry(string Address, IReadOnlyList<string> Names, string? Comment, bool Enabled);

/// <summary>
/// A hosts file that keeps every untouched line byte-for-byte (comments, blank lines, spacing) and re-serializes only the
/// entries that were added, changed or removed. <see cref="Validate"/> is the one rule set both the editor and the
/// elevated writers use, so what gets written as administrator is always a well-formed hosts file.
/// </summary>
public sealed partial class HostsDocument
{
    public const int MaxBytes = 32 * 1024;
    private const int MaxLineLength = 1000;

    private readonly List<Line> _lines = [];

    private sealed class Line(string raw, HostsEntry? entry)
    {
        public string Raw { get; set; } = raw;

        public HostsEntry? Entry { get; set; } = entry;
    }

    public bool UsesCrLf { get; private set; }

    public IReadOnlyList<HostsEntry> Entries => [.. _lines.Where(l => l.Entry is not null).Select(l => l.Entry!)];

    public static HostsDocument Parse(string text)
    {
        var document = new HostsDocument { UsesCrLf = text.Contains("\r\n", StringComparison.Ordinal) };
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        if (text.Length == 0)
        {
            return document;
        }

        foreach (var raw in normalized.Split('\n'))
        {
            document._lines.Add(new Line(raw, TryParseLine(raw)));
        }

        return document;
    }

    public string Serialize()
    {
        var newline = UsesCrLf ? "\r\n" : "\n";
        var text = new StringBuilder();
        foreach (var line in _lines)
        {
            text.Append(line.Raw).Append(newline);
        }

        return text.ToString();
    }

    public void Add(HostsEntry entry)
    {
        RequireValid(entry);
        _lines.Add(new Line(Format(entry), entry));
    }

    public void Replace(int index, HostsEntry entry)
    {
        RequireValid(entry);
        var line = EntryLine(index);
        line.Entry = entry;
        line.Raw = Format(entry);
    }

    public void Remove(int index) => _lines.Remove(EntryLine(index));

    public void SetEnabled(int index, bool enabled) =>
        Replace(index, EntryLine(index).Entry! with { Enabled = enabled });

    /// <summary>Entries whose <see cref="HostsEntry.Names"/> appear on more than one enabled line (the first wins in a resolver).</summary>
    public IReadOnlySet<string> Duplicates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries.Where(e => e.Enabled))
        {
            foreach (var name in entry.Names)
            {
                if (!seen.Add(entry.Address.Contains(':', StringComparison.Ordinal) ? name + "/6" : name + "/4"))
                {
                    duplicates.Add(name);
                }
            }
        }

        return duplicates;
    }

    /// <summary>Throws <see cref="ArgumentException"/> unless <paramref name="text"/> is a well-formed hosts file small enough to apply.</summary>
    public static void Validate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
        {
            throw new ArgumentException($"The hosts file is larger than {MaxBytes / 1024} KB.");
        }

        var number = 0;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            number++;
            if (raw.Length > MaxLineLength || raw.Any(c => char.IsControl(c) && c != '\t'))
            {
                throw new ArgumentException($"Line {number} is too long or contains control characters.");
            }

            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            if (TryParseLine(raw) is null)
            {
                throw new ArgumentException($"Line {number} is not a hosts entry (expected: address name [name…]): {Shorten(raw)}");
            }
        }
    }

    public static bool IsValidHostname(string name) =>
        name.Length is > 0 and <= 253 && HostName().IsMatch(name);

    public static bool IsValidAddress(string address) =>
        address.Length <= 45 && IPAddress.TryParse(address, out var ip) && ip.ToString().Length > 0 && !address.Contains('%', StringComparison.Ordinal);

    /// <summary>Parses <c>addr name… # comment</c> (also after a leading <c>#</c>, giving a disabled entry); null when the line is not an entry.</summary>
    public static HostsEntry? TryParseLine(string raw)
    {
        var text = raw.Trim();
        var enabled = true;
        if (text.StartsWith('#'))
        {
            enabled = false;
            text = text.TrimStart('#').Trim();
        }

        string? comment = null;
        if (text.IndexOf('#') is var hash and >= 0)
        {
            comment = text[(hash + 1)..].Trim();
            text = text[..hash].Trim();
        }

        var parts = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !IsValidAddress(parts[0]) || !parts.Skip(1).All(IsValidHostname))
        {
            return null;
        }

        return new HostsEntry(parts[0], [.. parts.Skip(1)], string.IsNullOrEmpty(comment) ? null : comment, enabled);
    }

    public static string Format(HostsEntry entry)
    {
        var line = $"{entry.Address,-15} {string.Join(' ', entry.Names)}" + (entry.Comment is { Length: > 0 } c ? "  # " + c : string.Empty);
        return entry.Enabled ? line : "# " + line;
    }

    private static void RequireValid(HostsEntry entry)
    {
        if (!IsValidAddress(entry.Address))
        {
            throw new ArgumentException($"'{entry.Address}' is not an IP address.");
        }

        if (entry.Names.Count == 0 || !entry.Names.All(IsValidHostname))
        {
            throw new ArgumentException("A hosts entry needs one or more host names (letters, digits, - _ . only).");
        }

        if (entry.Comment is not null && entry.Comment.Any(char.IsControl))
        {
            throw new ArgumentException("The comment contains control characters.");
        }
    }

    private Line EntryLine(int index)
    {
        var lines = _lines.Where(l => l.Entry is not null).ToList();
        return index >= 0 && index < lines.Count ? lines[index] : throw new ArgumentOutOfRangeException(nameof(index));
    }

    private static string Shorten(string text) => text.Length <= 60 ? text.Trim() : text.Trim()[..57] + "…";

    [GeneratedRegex(@"^(?!-)[A-Za-z0-9_-]{1,63}(?:\.[A-Za-z0-9_-]{1,63})*\.?$")]
    private static partial Regex HostName();
}

/// <summary>Reads and writes the machine's hosts file; writing is privileged and goes through the OS's own mechanism.</summary>
public interface IHostsService
{
    string Path { get; }

    Task<HostsDocument> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates and writes <paramref name="document"/> (a backup is kept next to the file).</summary>
    Task<ServiceOperationResult> WriteAsync(HostsDocument document, CancellationToken cancellationToken = default);
}
