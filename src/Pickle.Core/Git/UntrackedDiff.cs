using System.Globalization;
using System.Text;

namespace Pickle.Core.Git;

/// <summary>
/// Builds the "new file" diff git would print for an untracked file (git diff ignores untracked files), so the panel
/// can show it and stage parts of it with <c>git apply --cached</c>.
/// </summary>
internal static class UntrackedDiff
{
    private const int MaxTextBytes = 4 * 1024 * 1024;

    public static string Build(string root, string relativePath)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var header = new StringBuilder();
        var a = Quote("a/" + relativePath);
        var b = Quote("b/" + relativePath);
        header.Append("diff --git ").Append(a).Append(' ').Append(b).Append('\n');

        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
            if (info.LinkTarget is { } target)
            {
                header.Append("new file mode 120000\n");
                return AppendText(header, b, target, endsWithNewline: false);
            }

            if (!info.Exists)
            {
                return string.Empty;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }

        header.Append("new file mode ").Append(IsExecutable(info) ? "100755" : "100644").Append('\n');
        if (info.Length == 0)
        {
            return header.ToString();
        }

        byte[] bytes;
        try
        {
            if (info.Length > MaxTextBytes)
            {
                return header.Append("Binary files /dev/null and ").Append(b).Append(" differ\n").ToString();
            }

            bytes = File.ReadAllBytes(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }

        // git's own heuristic: a NUL byte in the first 8000 bytes means binary.
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0))
        {
            return header.Append("Binary files /dev/null and ").Append(b).Append(" differ\n").ToString();
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8: a lossy text patch would write mangled bytes into the index, so offer whole-file staging only.
            return header.Append("Binary files /dev/null and ").Append(b).Append(" differ\n").ToString();
        }

        return AppendText(header, b, text, text.EndsWith('\n'));
    }

    private static string AppendText(StringBuilder header, string b, string text, bool endsWithNewline)
    {
        var body = endsWithNewline ? text[..^1] : text;
        var lines = body.Split('\n');
        header.Append("--- /dev/null\n+++ ").Append(b).Append('\n');
        header.Append("@@ -0,0 +1");
        if (lines.Length != 1)
        {
            header.Append(',').Append(lines.Length.ToString(CultureInfo.InvariantCulture));
        }

        header.Append(" @@\n");
        foreach (var line in lines)
        {
            header.Append('+').Append(line).Append('\n');
        }

        if (!endsWithNewline)
        {
            header.Append("\\ No newline at end of file\n");
        }

        return header.ToString();
    }

    private static bool IsExecutable(FileInfo info) =>
        !OperatingSystem.IsWindows() && (info.UnixFileMode & UnixFileMode.UserExecute) != 0;

    /// <summary>C-style quoting git applies to paths containing quotes, backslashes or control characters.</summary>
    internal static string Quote(string path)
    {
        var needs = false;
        foreach (var c in path)
        {
            if (c is '"' or '\\' || c < ' ' || c == '\x7f')
            {
                needs = true;
                break;
            }
        }

        if (!needs)
        {
            return path;
        }

        var sb = new StringBuilder(path.Length + 4).Append('"');
        foreach (var c in path)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                default:
                    if (c < ' ' || c == '\x7f')
                    {
                        sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }
}
