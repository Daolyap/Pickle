using System.Globalization;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Git;

/// <summary>Parses <c>git status --porcelain=v2 --branch --show-stash -z</c>.</summary>
internal static class GitStatusParser
{
    public static GitStatus Parse(string output, string root)
    {
        string? oid = null;
        string? head = null;
        string? upstream = null;
        int ahead = 0, behind = 0, stashes = 0;
        var entries = new List<GitStatusEntry>();

        var records = output.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Length < 2)
            {
                continue;
            }

            switch (record[0])
            {
                case '#':
                    ParseHeader(record, ref oid, ref head, ref upstream, ref ahead, ref behind, ref stashes);
                    break;
                case '1':
                {
                    // 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
                    var parts = record.Split(' ', 9);
                    if (parts.Length == 9 && parts[1].Length == 2)
                    {
                        entries.Add(new GitStatusEntry(parts[8], null, Kind(parts[1][0]), Kind(parts[1][1])));
                    }

                    break;
                }

                case '2':
                {
                    // 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <X><score> <path>\0<origPath>
                    var parts = record.Split(' ', 10);
                    var original = i + 1 < records.Length ? records[++i] : null;
                    if (parts.Length == 10 && parts[1].Length == 2)
                    {
                        entries.Add(new GitStatusEntry(parts[9], string.IsNullOrEmpty(original) ? null : original, Kind(parts[1][0]), Kind(parts[1][1])));
                    }

                    break;
                }

                case 'u':
                {
                    // u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>
                    // Conflicts count as unstaged work (not staged): the index holds conflict stages, nothing committable.
                    var parts = record.Split(' ', 11);
                    if (parts.Length == 11)
                    {
                        entries.Add(new GitStatusEntry(parts[10], null, GitChangeKind.None, GitChangeKind.Unmerged));
                    }

                    break;
                }

                case '?':
                    entries.Add(new GitStatusEntry(record[2..], null, GitChangeKind.Untracked, GitChangeKind.Untracked));
                    break;
                case '!':
                    entries.Add(new GitStatusEntry(record[2..], null, GitChangeKind.Ignored, GitChangeKind.Ignored));
                    break;
            }
        }

        var detached = head == "(detached)";
        return new GitStatus(
            Root: root,
            Branch: detached ? null : head,
            Upstream: upstream,
            Ahead: ahead,
            Behind: behind,
            IsDetached: detached,
            HeadSha: oid is null or "(initial)" ? null : oid,
            Entries: entries,
            StashCount: stashes);
    }

    private static void ParseHeader(string record, ref string? oid, ref string? head, ref string? upstream, ref int ahead, ref int behind, ref int stashes)
    {
        var parts = record.Split(' ', 3);
        if (parts.Length < 3)
        {
            return;
        }

        switch (parts[1])
        {
            case "branch.oid":
                oid = parts[2];
                break;
            case "branch.head":
                head = parts[2];
                break;
            case "branch.upstream":
                upstream = parts[2];
                break;
            case "branch.ab":
                foreach (var token in parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.Length > 1 && int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    {
                        if (token[0] == '+')
                        {
                            ahead = n;
                        }
                        else if (token[0] == '-')
                        {
                            behind = n;
                        }
                    }
                }

                break;
            case "stash":
                _ = int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out stashes);
                break;
        }
    }

    private static GitChangeKind Kind(char c) => c switch
    {
        'M' => GitChangeKind.Modified,
        'T' => GitChangeKind.TypeChanged,
        'A' => GitChangeKind.Added,
        'D' => GitChangeKind.Deleted,
        'R' => GitChangeKind.Renamed,
        'C' => GitChangeKind.Copied,
        'U' => GitChangeKind.Unmerged,
        '?' => GitChangeKind.Untracked,
        '!' => GitChangeKind.Ignored,
        _ => GitChangeKind.None,
    };

    /// <summary>The repository's git directory (follows a <c>.git</c> file's <c>gitdir:</c> for worktrees/submodules).</summary>
    public static string? ResolveGitDir(string root)
    {
        var dotGit = Path.Combine(root, ".git");
        if (Directory.Exists(dotGit))
        {
            return dotGit;
        }

        if (!File.Exists(dotGit))
        {
            return null;
        }

        try
        {
            using var reader = new StreamReader(dotGit);
            var line = reader.ReadLine()?.Trim();
            const string prefix = "gitdir:";
            if (line is not null && line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var target = line[prefix.Length..].Trim();
                return target.Length == 0 ? null : Path.GetFullPath(Path.Combine(root, target));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        return null;
    }

    /// <summary>In-progress operation: rebase, am, merge, cherry-pick, revert or bisect (null when none).</summary>
    public static string? DetectOperation(string? gitDir)
    {
        if (gitDir is null)
        {
            return null;
        }

        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")))
        {
            return "rebase";
        }

        var rebaseApply = Path.Combine(gitDir, "rebase-apply");
        if (Directory.Exists(rebaseApply))
        {
            return File.Exists(Path.Combine(rebaseApply, "applying")) ? "am" : "rebase";
        }

        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD")))
        {
            return "merge";
        }

        if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
        {
            return "cherry-pick";
        }

        if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
        {
            return "revert";
        }

        return File.Exists(Path.Combine(gitDir, "BISECT_LOG")) ? "bisect" : null;
    }

    /// <summary>Walks up from <paramref name="path"/> to the directory containing <c>.git</c> (no process spawned).</summary>
    public static string? FindRoot(string path)
    {
        string? directory;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (File.Exists(directory))
            {
                directory = Path.GetDirectoryName(directory);
            }
            else if (!Directory.Exists(directory))
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
        {
            return null;
        }

        while (!string.IsNullOrEmpty(directory))
        {
            var dotGit = Path.Combine(directory, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }
}
