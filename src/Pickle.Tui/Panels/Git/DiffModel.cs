using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pickle.Tui.Panels.Git;

public enum DiffLineKind
{
    Context,
    Added,
    Removed,

    /// <summary><c>\ No newline at end of file</c>; belongs to the line before it.</summary>
    NoNewline,
}

/// <summary>One hunk body line; <see cref="Text"/> is the raw line including its prefix character.</summary>
public sealed record DiffLine(DiffLineKind Kind, string Text)
{
    public string Content => Text.Length > 0 ? Text[1..] : string.Empty;

    public bool IsChange => Kind is DiffLineKind.Added or DiffLineKind.Removed;
}

public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, string Header, IReadOnlyList<DiffLine> Lines)
{
    /// <summary>The text after the closing <c>@@</c> (usually the enclosing function).</summary>
    public string Section
    {
        get
        {
            var close = Header.IndexOf("@@", 2, StringComparison.Ordinal);
            return close < 0 ? string.Empty : Header[(close + 2)..].Trim();
        }
    }
}

public sealed record DiffFile(IReadOnlyList<string> HeaderLines, string? OldPath, string? NewPath, bool IsBinary, bool IsCombined, IReadOnlyList<DiffHunk> Hunks)
{
    public string Path => NewPath ?? OldPath ?? string.Empty;

    public bool IsNewFile => HeaderLines.Any(l => l.StartsWith("new file mode", StringComparison.Ordinal));

    public bool IsDeletedFile => HeaderLines.Any(l => l.StartsWith("deleted file mode", StringComparison.Ordinal));

    /// <summary>Whether hunks/lines of this file can be staged with <c>git apply --cached</c>.</summary>
    public bool CanStagePartially => !IsBinary && !IsCombined && Hunks.Count > 0;
}

/// <summary>
/// Unified-diff parser plus patch builders for hunk and line-level staging. Patches are meant for
/// <c>git apply --cached [--reverse] --recount</c>; line endings (including CR) are preserved byte for byte.
/// </summary>
public static partial class DiffParser
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeaderRegex();

    public static IReadOnlyList<DiffFile> Parse(string? diff)
    {
        var files = new List<DiffFile>();
        if (string.IsNullOrEmpty(diff))
        {
            return files;
        }

        var lines = diff.Split('\n');
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;

        List<string>? header = null;
        List<DiffHunk> hunks = [];
        List<DiffLine>? body = null;
        string? hunkHeader = null;
        int oldStart = 0, oldCount = 0, newStart = 0, newCount = 0;
        var combined = false;

        void FlushHunk()
        {
            if (body is not null && hunkHeader is not null)
            {
                hunks.Add(new DiffHunk(oldStart, oldCount, newStart, newCount, hunkHeader, body));
            }

            body = null;
            hunkHeader = null;
        }

        void FlushFile()
        {
            FlushHunk();
            if (header is not null)
            {
                files.Add(BuildFile(header, hunks, combined));
            }

            header = null;
            hunks = [];
            combined = false;
        }

        for (var i = 0; i < count; i++)
        {
            var line = lines[i];
            if (line.StartsWith("diff ", StringComparison.Ordinal))
            {
                FlushFile();
                header = [line];
                combined = line.StartsWith("diff --cc ", StringComparison.Ordinal) || line.StartsWith("diff --combined ", StringComparison.Ordinal);
                continue;
            }

            if (header is null)
            {
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                FlushHunk();
                hunkHeader = line.TrimEnd('\r');
                body = [];
                var m = HunkHeaderRegex().Match(hunkHeader);
                if (m.Success)
                {
                    oldStart = ParseInt(m.Groups[1].Value);
                    oldCount = m.Groups[2].Success ? ParseInt(m.Groups[2].Value) : 1;
                    newStart = ParseInt(m.Groups[3].Value);
                    newCount = m.Groups[4].Success ? ParseInt(m.Groups[4].Value) : 1;
                }
                else
                {
                    oldStart = oldCount = newStart = newCount = 0;
                }

                continue;
            }

            if (body is null)
            {
                header.Add(line);
                continue;
            }

            body.Add(ToLine(line, combined));
        }

        FlushFile();
        return files;
    }

    /// <summary>A patch containing just <paramref name="hunk"/> (apply forward to stage, reverse to unstage).</summary>
    public static string BuildHunkPatch(DiffFile file, DiffHunk hunk)
    {
        var sb = new StringBuilder();
        foreach (var line in file.HeaderLines)
        {
            sb.Append(line).Append('\n');
        }

        sb.Append(hunk.Header).Append('\n');
        foreach (var line in hunk.Lines)
        {
            sb.Append(line.Text).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// A patch that applies only the selected change lines (indexes into <see cref="DiffHunk.Lines"/>) of a hunk.
    /// Forward (staging a worktree diff): unselected <c>-</c> lines become context, unselected <c>+</c> lines are
    /// dropped. Reverse (unstaging a <c>--cached</c> diff, applied with <c>--reverse</c>): the roles swap.
    /// Returns null when no change line is selected.
    /// </summary>
    public static string? BuildLinePatch(DiffFile file, DiffHunk hunk, IReadOnlySet<int> selected, bool reverse)
    {
        var lines = new List<DiffLine>(hunk.Lines.Count);
        var any = false;
        var keptPrevious = true;
        for (var i = 0; i < hunk.Lines.Count; i++)
        {
            var line = hunk.Lines[i];
            switch (line.Kind)
            {
                case DiffLineKind.Context:
                    lines.Add(line);
                    keptPrevious = true;
                    break;
                case DiffLineKind.NoNewline:
                    if (keptPrevious)
                    {
                        lines.Add(line);
                    }

                    break;
                default:
                    if (selected.Contains(i))
                    {
                        lines.Add(line);
                        any = true;
                        keptPrevious = true;
                        break;
                    }

                    // The side that already holds this line keeps it as context; the other side never sees it.
                    var keepAsContext = reverse ? line.Kind == DiffLineKind.Added : line.Kind == DiffLineKind.Removed;
                    if (keepAsContext)
                    {
                        lines.Add(new DiffLine(DiffLineKind.Context, " " + line.Content));
                    }

                    keptPrevious = keepAsContext;
                    break;
            }
        }

        if (!any)
        {
            return null;
        }

        var oldCount = lines.Count(l => l.Kind is DiffLineKind.Context or DiffLineKind.Removed);
        var newCount = lines.Count(l => l.Kind is DiffLineKind.Context or DiffLineKind.Added);
        var header = new List<string>(file.HeaderLines);
        var oldStart = hunk.OldStart;
        var newStart = hunk.NewStart;

        // A partial new/deleted-file patch no longer creates/deletes the file: rewrite it as a modification.
        if (file.IsNewFile && oldCount > 0)
        {
            header.RemoveAll(l => l.StartsWith("new file mode", StringComparison.Ordinal));
            ReplaceHeader(header, "--- ", "--- " + PrefixedPath("a/", file.Path));
            oldStart = 1;
        }

        if (file.IsDeletedFile && newCount > 0)
        {
            header.RemoveAll(l => l.StartsWith("deleted file mode", StringComparison.Ordinal));
            ReplaceHeader(header, "+++ ", "+++ " + PrefixedPath("b/", file.Path));
            newStart = 1;
        }

        var sb = new StringBuilder();
        foreach (var line in header)
        {
            sb.Append(line).Append('\n');
        }

        sb.Append("@@ -").Append(Range(oldStart, oldCount)).Append(" +").Append(Range(newStart, newCount)).Append(" @@");
        if (hunk.Section.Length > 0)
        {
            sb.Append(' ').Append(hunk.Section);
        }

        sb.Append('\n');
        foreach (var line in lines)
        {
            sb.Append(line.Text).Append('\n');
        }

        return sb.ToString();
    }

    private static string Range(int start, int count) =>
        count == 1 ? start.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{start},{count}");

    private static void ReplaceHeader(List<string> header, string prefix, string replacement)
    {
        var index = header.FindIndex(l => l.StartsWith(prefix, StringComparison.Ordinal));
        if (index >= 0)
        {
            header[index] = replacement;
        }
    }

    private static string PrefixedPath(string prefix, string path) => QuoteIfNeeded(prefix + path);

    private static DiffLine ToLine(string line, bool combined)
    {
        if (line.Length == 0)
        {
            return new DiffLine(DiffLineKind.Context, " ");
        }

        if (combined)
        {
            var marks = line.Length >= 2 ? line[..2] : line;
            var kind = marks.Contains('+', StringComparison.Ordinal) ? DiffLineKind.Added
                : marks.Contains('-', StringComparison.Ordinal) ? DiffLineKind.Removed
                : line[0] == '\\' ? DiffLineKind.NoNewline
                : DiffLineKind.Context;
            return new DiffLine(kind, line);
        }

        return line[0] switch
        {
            '+' => new DiffLine(DiffLineKind.Added, line),
            '-' => new DiffLine(DiffLineKind.Removed, line),
            '\\' => new DiffLine(DiffLineKind.NoNewline, line),
            _ => new DiffLine(DiffLineKind.Context, line),
        };
    }

    private static DiffFile BuildFile(List<string> header, List<DiffHunk> hunks, bool combined)
    {
        string? oldPath = null, newPath = null;
        bool sawOld = false, sawNew = false, binary = false;
        foreach (var raw in header)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                oldPath = ParsePath(line[4..], "a/");
                sawOld = true;
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                newPath = ParsePath(line[4..], "b/");
                sawNew = true;
            }
            else if (line.StartsWith("rename from ", StringComparison.Ordinal) || line.StartsWith("copy from ", StringComparison.Ordinal))
            {
                oldPath ??= Unquote(line[(line.IndexOf(" from ", StringComparison.Ordinal) + 6)..]);
                sawOld = true;
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal) || line.StartsWith("copy to ", StringComparison.Ordinal))
            {
                newPath ??= Unquote(line[(line.IndexOf(" to ", StringComparison.Ordinal) + 4)..]);
                sawNew = true;
            }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch")
            {
                binary = true;
            }
        }

        if (!sawOld && !sawNew)
        {
            var first = header[0].TrimEnd('\r');
            if (combined)
            {
                var name = first[(first.IndexOf(' ', 5) + 1)..];
                oldPath = newPath = Unquote(name);
            }
            else if (first.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var rest = first["diff --git ".Length..];
                if (rest.StartsWith('"'))
                {
                    var end = FindClosingQuote(rest);
                    oldPath = StripPrefix(Unquote(rest[..(end + 1)]), "a/");
                    newPath = StripPrefix(Unquote(rest[(end + 1)..].TrimStart()), "b/");
                }
                else if (rest.Length >= 5 && (rest.Length - 1) % 2 == 0)
                {
                    // "a/P b/P": without a rename both halves are the same path, even when it contains spaces.
                    var half = (rest.Length - 1) / 2;
                    oldPath = StripPrefix(rest[..half], "a/");
                    newPath = StripPrefix(rest[(half + 1)..], "b/");
                }
            }
        }

        return new DiffFile(header, oldPath, newPath, binary, combined, hunks);
    }

    private static string? ParsePath(string value, string prefix)
    {
        // git appends a TAB after paths that contain spaces in ---/+++ lines.
        var tab = value.IndexOf('\t', StringComparison.Ordinal);
        if (tab >= 0 && !value.StartsWith('"'))
        {
            value = value[..tab];
        }

        value = Unquote(value.TrimEnd('\r'));
        return value == "/dev/null" ? null : StripPrefix(value, prefix);
    }

    private static string StripPrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;

    private static int FindClosingQuote(string s)
    {
        for (var i = 1; i < s.Length; i++)
        {
            if (s[i] == '\\')
            {
                i++;
            }
            else if (s[i] == '"')
            {
                return i;
            }
        }

        return s.Length - 1;
    }

    /// <summary>Reverses git's C-style path quoting (octal escapes are UTF-8 bytes).</summary>
    internal static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"')
        {
            return value;
        }

        var end = FindClosingQuote(value);
        var bytes = new List<byte>(value.Length);
        for (var i = 1; i < end; i++)
        {
            var c = value[i];
            if (c != '\\' || i + 1 >= end)
            {
                var length = char.IsHighSurrogate(c) && i + 1 < end ? 2 : 1;
                bytes.AddRange(Encoding.UTF8.GetBytes(value.Substring(i, length)));
                i += length - 1;
                continue;
            }

            var next = value[++i];
            switch (next)
            {
                case 'n': bytes.Add((byte)'\n'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case 'a': bytes.Add(7); break;
                case 'b': bytes.Add(8); break;
                case 'f': bytes.Add(12); break;
                case 'v': bytes.Add(11); break;
                case >= '0' and <= '7' when i + 2 < end:
                    bytes.Add((byte)Convert.ToInt32(value.Substring(i, 3), 8));
                    i += 2;
                    break;
                default:
                    bytes.AddRange(Encoding.UTF8.GetBytes(next.ToString()));
                    break;
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static string QuoteIfNeeded(string path)
    {
        if (!path.Any(c => c is '"' or '\\' || c < ' '))
        {
            return path;
        }

        var sb = new StringBuilder("\"");
        foreach (var c in path)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\t' => "\\t",
                '\n' => "\\n",
                '\r' => "\\r",
                < ' ' => "\\" + Convert.ToString(c, 8).PadLeft(3, '0'),
                _ => c.ToString(),
            });
        }

        return sb.Append('"').ToString();
    }

    private static int ParseInt(string s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
