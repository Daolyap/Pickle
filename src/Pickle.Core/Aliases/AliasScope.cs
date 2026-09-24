using System.Management.Automation;
using System.Text;
using System.Text.RegularExpressions;
using Pickle.Abstractions;

namespace Pickle.Core.Aliases;

/// <summary>
/// Directory and machine scoping. A directory scope is a path or glob: <c>~</c> is the home directory, <c>*</c> matches
/// within one segment, <c>**</c> any depth, and a trailing <c>/**</c> includes the directory itself. A scope without
/// wildcards covers that directory and everything below it; a relative glob matches anywhere (e.g. <c>**/node_modules</c>).
/// </summary>
public static class AliasScope
{
    public static bool IsActive(AliasDefinition alias, string cwd, string machineName) =>
        MatchesMachine(alias.MachineScope, machineName) && MatchesDirectory(alias.DirectoryScope, cwd);

    public static bool MatchesMachine(string? scope, string machineName) =>
        string.IsNullOrWhiteSpace(scope)
        || scope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => WildcardPattern.Get(s, WildcardOptions.IgnoreCase).IsMatch(machineName));

    public static bool MatchesDirectory(string? glob, string cwd, string? home = null, bool? ignoreCase = null)
    {
        if (string.IsNullOrWhiteSpace(glob))
        {
            return true;
        }

        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var caseInsensitive = ignoreCase ?? (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
        var pattern = glob.Trim();
        if (pattern == "~" || pattern.StartsWith("~/", StringComparison.Ordinal) || pattern.StartsWith("~\\", StringComparison.Ordinal))
        {
            pattern = home + pattern[1..];
        }

        pattern = Normalize(pattern);
        var path = Normalize(cwd);
        var comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (pattern.IndexOfAny(['*', '?', '[']) < 0)
        {
            return string.Equals(path, pattern, comparison)
                || path.StartsWith(pattern.EndsWith('/') ? pattern : pattern + "/", comparison);
        }

        var rooted = pattern.StartsWith('/') || (pattern.Length > 1 && pattern[1] == ':');
        var regex = new StringBuilder(rooted ? "^" : "^(?:.*/)?");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                var slashBefore = i > 0 && pattern[i - 1] == '/';
                var atEnd = i + 2 == pattern.Length;
                if (slashBefore && atEnd)
                {
                    regex.Length--;
                    regex.Append("(?:/.*)?");
                }
                else if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                {
                    regex.Append("(?:.*/)?");
                    i++;
                }
                else
                {
                    regex.Append(".*");
                }

                i++;
            }
            else if (c == '*')
            {
                regex.Append("[^/]*");
            }
            else if (c == '?')
            {
                regex.Append("[^/]");
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
            }
        }

        regex.Append('$');
        var options = RegexOptions.CultureInvariant | (caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None);
        return Regex.IsMatch(path, regex.ToString(), options, TimeSpan.FromMilliseconds(200));
    }

    private static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        while (p.Length > 1 && p.EndsWith('/') && !(p.Length == 3 && p[1] == ':'))
        {
            p = p[..^1];
        }

        return p;
    }
}
