namespace Pickle.Core.Git;

/// <summary>Validation for names passed to git as arguments (mirrors <c>git check-ref-format --branch</c>).</summary>
public static class GitRefNames
{
    public static bool IsValidBranchName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name[0] == '-' || name is "@" or "HEAD")
        {
            return false;
        }

        if (name.StartsWith('/') || name.EndsWith('/') || name.EndsWith('.')
            || name.Contains("//", StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal)
            || name.Contains("@{", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (char.IsControl(c) || c is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\')
            {
                return false;
            }
        }

        foreach (var component in name.Split('/'))
        {
            if (component.StartsWith('.') || component.EndsWith(".lock", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A revision safe to pass positionally (branch, tag, sha, <c>origin/x</c>, <c>HEAD~2</c>): never an option.</summary>
    public static bool IsSafeRevision(string? revision)
    {
        if (string.IsNullOrEmpty(revision) || revision[0] == '-')
        {
            return false;
        }

        foreach (var c in revision)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }
}
