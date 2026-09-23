using System.Globalization;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Git;

/// <summary>Parsers for the machine-readable formats GitService asks git for.</summary>
internal static class GitOutputParsers
{
    public const char FieldSeparator = '\x1f';

    public const string LogFormat = "--format=%x1f%H%x1f%h%x1f%an%x1f%aI%x1f%s%x1f%D%x1e";

    public const string BranchFormat = "--format=%(refname)%1f%(HEAD)%1f%(upstream:short)%1f%(committerdate:iso-strict)%1f%(symref)%1f%(contents:subject)";

    public const string StashFormat = "--format=%gd%x1f%s";

    /// <summary>
    /// Parses <see cref="LogFormat"/> output. With <c>--graph</c> each commit line is prefixed by its graph column;
    /// connector-only lines (<c>|\</c>, <c>|/</c>) carry no commit and are skipped.
    /// </summary>
    public static IReadOnlyList<GitCommit> ParseLog(string output)
    {
        var commits = new List<GitCommit>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var start = line.IndexOf(FieldSeparator, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            var fields = line[(start + 1)..].TrimEnd('\x1e').Split(FieldSeparator);
            if (fields.Length < 6)
            {
                continue;
            }

            DateTimeOffset.TryParse(fields[3], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
            IReadOnlyList<string> refs = fields[5].Length == 0
                ? []
                : fields[5].Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            commits.Add(new GitCommit(fields[0], fields[1], fields[2], date, fields[4], refs, line[..start].TrimEnd()));
        }

        return commits;
    }

    public static IReadOnlyList<GitBranch> ParseBranches(string output)
    {
        var branches = new List<GitBranch>();
        foreach (var rawLine in output.Split('\n'))
        {
            var fields = rawLine.TrimEnd('\r').Split(FieldSeparator, 6);
            if (fields.Length < 6 || fields[4].Length > 0)
            {
                // fields[4] is %(symref): skip symbolic refs such as refs/remotes/origin/HEAD.
                continue;
            }

            string name;
            bool remote;
            if (fields[0].StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                name = fields[0]["refs/heads/".Length..];
                remote = false;
            }
            else if (fields[0].StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                name = fields[0]["refs/remotes/".Length..];
                remote = true;
            }
            else
            {
                continue;
            }

            DateTimeOffset? date = DateTimeOffset.TryParse(fields[3], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
            branches.Add(new GitBranch(
                name,
                IsCurrent: fields[1] == "*",
                IsRemote: remote,
                Upstream: fields[2].Length == 0 ? null : fields[2],
                LastCommitSubject: fields[5].Length == 0 ? null : fields[5],
                LastCommitDate: date));
        }

        return branches;
    }

    public static IReadOnlyList<GitStash> ParseStashes(string output)
    {
        var stashes = new List<GitStash>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split(FieldSeparator, 2);
            var name = fields[0];
            var index = stashes.Count;
            var open = name.IndexOf("@{", StringComparison.Ordinal);
            if (open >= 0 && name.EndsWith('}') && int.TryParse(name.AsSpan(open + 2, name.Length - open - 3), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                index = n;
            }

            stashes.Add(new GitStash(index, name, fields.Length > 1 ? fields[1] : string.Empty));
        }

        return stashes;
    }

    /// <summary>Splits NUL-separated output (<c>-z</c>) into non-empty items.</summary>
    public static IReadOnlyList<string> SplitNul(string output) =>
        output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    public static IReadOnlyList<string> SplitLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
