using Pickle.Abstractions;

namespace Pickle.Core.Prompt.Segments;

/// <summary>
/// Current directory. Options: <c>maxDepth</c> (collapse the middle with …), <c>tildeHome</c> (default true),
/// <c>boldRepoRoot</c> (default true). Paths are handled as strings so Windows paths format the same on any OS.
/// </summary>
public sealed class CwdSegment(SegmentEnvironment environment) : IPromptSegment
{
    public const string Ellipsis = "…";

    public string Type => "cwd";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken) =>
        SegmentText.Show(Render(context.Cwd, style));

    public string Render(string cwd, SegmentStyle style) => Format(
        cwd,
        style.OptionBool("tildeHome", true) ? environment.Home() : null,
        style.OptionInt("maxDepth", 0),
        style.OptionBool("boldRepoRoot", true) ? environment.FindRepositoryRoot(cwd) : null);

    public static string Format(string cwd, string? home, int maxDepth = 0, string? repoRoot = null)
    {
        var path = ParsedPath.Parse(cwd);
        var parts = path.Parts;
        var root = path.Root;
        var offset = 0;

        if (home is not null && ParsedPath.Parse(home) is { Parts.Count: > 0 } homePath && path.StartsWith(homePath))
        {
            root = "~";
            offset = homePath.Parts.Count;
            parts = parts.GetRange(offset, parts.Count - offset);
        }

        var repoIndex = -1;
        if (repoRoot is not null && ParsedPath.Parse(repoRoot) is { Parts.Count: > 0 } repoPath && path.StartsWith(repoPath))
        {
            repoIndex = repoPath.Parts.Count - 1 - offset;
        }

        var kept = Keep(parts.Count, maxDepth, repoIndex);
        var display = new List<string>(parts.Count);
        var previous = -1;
        foreach (var index in kept)
        {
            if (index - previous > 1)
            {
                display.Add(Ellipsis);
            }

            var name = SegmentText.Sanitize(parts[index]);
            display.Add(index == repoIndex ? Ansi.Bold + name + "\u001b[22m" : name);
            previous = index;
        }

        return path.Compose(SegmentText.Sanitize(root), display);
    }

    /// <summary>Indices to show: the first component, the repository root, and as many trailing ones as fit.</summary>
    private static SortedSet<int> Keep(int count, int maxDepth, int repoIndex)
    {
        var kept = new SortedSet<int>();
        if (maxDepth <= 0 || count <= maxDepth)
        {
            for (var i = 0; i < count; i++)
            {
                kept.Add(i);
            }

            return kept;
        }

        if (maxDepth == 1)
        {
            kept.Add(count - 1);
            return kept;
        }

        kept.Add(0);
        var slots = maxDepth - 1;
        if (repoIndex > 0 && repoIndex < count - slots && slots >= 2)
        {
            kept.Add(repoIndex);
            slots--;
        }

        for (var i = count - slots; i < count; i++)
        {
            kept.Add(i);
        }

        return kept;
    }

    private sealed record ParsedPath(string Root, List<string> Parts, char Separator, bool Windows)
    {
        public static ParsedPath Parse(string path)
        {
            var windows = path.Contains('\\', StringComparison.Ordinal) || (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]));
            var separator = windows ? '\\' : '/';
            var normalized = windows ? path.Replace('/', '\\') : path;
            string root;
            string rest;
            if (windows && normalized.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var unc = normalized[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
                root = @"\\" + string.Join('\\', unc.Take(2));
                rest = string.Join('\\', unc.Skip(2));
            }
            else if (windows && normalized.Length >= 2 && normalized[1] == ':')
            {
                root = normalized[..2];
                rest = normalized[2..];
            }
            else if (normalized.StartsWith(separator))
            {
                root = separator.ToString();
                rest = normalized;
            }
            else
            {
                root = string.Empty;
                rest = normalized;
            }

            return new ParsedPath(root, [.. rest.Split(separator, StringSplitOptions.RemoveEmptyEntries)], separator, windows);
        }

        public bool StartsWith(ParsedPath prefix)
        {
            var comparison = Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (prefix.Windows != Windows || !string.Equals(prefix.Root, Root, comparison) || prefix.Parts.Count > Parts.Count)
            {
                return false;
            }

            for (var i = 0; i < prefix.Parts.Count; i++)
            {
                if (!string.Equals(prefix.Parts[i], Parts[i], comparison))
                {
                    return false;
                }
            }

            return true;
        }

        public string Compose(string root, List<string> parts)
        {
            var joined = string.Join(Separator, parts);
            if (root == Separator.ToString())
            {
                return root + joined;
            }

            if (root.Length == 0)
            {
                return joined;
            }

            // A bare drive ("C:") is relative in Windows syntax; always show "C:\".
            var driveRoot = root.Length == 2 && root[1] == ':';
            return parts.Count > 0 || driveRoot ? root + Separator + joined : root;
        }
    }
}
