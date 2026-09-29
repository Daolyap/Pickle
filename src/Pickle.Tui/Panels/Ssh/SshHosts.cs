using System.Globalization;
using System.Text.RegularExpressions;

namespace Pickle.Tui.Panels.Ssh;

/// <summary>A host to connect to: an alias from ssh_config (with what it resolves to) or a known_hosts entry.</summary>
public sealed record SshHost(string Name, string? HostName, string? User, int? Port, string Source)
{
    /// <summary>"user@host:port" as far as known, for the list's detail column.</summary>
    public string Target =>
        (User is { } u ? u + "@" : string.Empty) + (HostName ?? Name) + (Port is { } p and not 22 ? ":" + p.ToString(CultureInfo.InvariantCulture) : string.Empty);

    /// <summary>
    /// The ssh arguments that reach this host: the alias for config entries (ssh applies their settings itself), the
    /// name and a non-default port for known_hosts entries.
    /// </summary>
    public IReadOnlyList<string> Arguments =>
        Source == SshHosts.KnownHostsSource && Port is { } port and not 22 ? ["-p", port.ToString(CultureInfo.InvariantCulture), Name] : [Name];
}

/// <summary>
/// Reads ~/.ssh/config (Host blocks, following Include) and ~/.ssh/known_hosts. Patterns (* ? !), hashed known_hosts
/// entries and names that aren't plain host names are left out.
/// </summary>
public static partial class SshHosts
{
    public const string ConfigSource = "config";
    public const string KnownHostsSource = "known_hosts";
    private const int MaxIncludeDepth = 5;

    /// <summary>Hosts from the config files and known_hosts under <paramref name="sshDirectory"/>; config entries first.</summary>
    public static List<SshHost> Load(string sshDirectory, string? home)
    {
        var hosts = new List<SshHost>();
        var config = Path.Combine(sshDirectory, "config");
        hosts.AddRange(ParseConfig(ReadAll(config, sshDirectory, home, 0)));
        if (Read(Path.Combine(sshDirectory, "known_hosts")) is { } known)
        {
            hosts.AddRange(ParseKnownHosts(known));
        }

        return [.. hosts.DistinctBy(h => h.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Host aliases from ssh_config text, with the HostName, User and Port set in their blocks.</summary>
    public static List<SshHost> ParseConfig(string text)
    {
        var hosts = new List<(string Name, Dictionary<string, string> Settings)>();
        List<(string Name, Dictionary<string, string> Settings)> current = [];
        foreach (var raw in text.Split('\n'))
        {
            var (keyword, value) = SplitLine(raw);
            if (keyword is null)
            {
                continue;
            }

            if (keyword.Equals("host", StringComparison.OrdinalIgnoreCase))
            {
                current = [.. value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(IsPlainHostName)
                    .Select(name => (name, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)))];
                hosts.AddRange(current);
            }
            else if (keyword.Equals("match", StringComparison.OrdinalIgnoreCase))
            {
                current = [];
            }
            else
            {
                foreach (var (_, settings) in current)
                {
                    settings.TryAdd(keyword, value);
                }
            }
        }

        return [.. hosts.Select(h => new SshHost(
            h.Name,
            h.Settings.GetValueOrDefault("hostname"),
            h.Settings.GetValueOrDefault("user"),
            int.TryParse(h.Settings.GetValueOrDefault("port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : null,
            ConfigSource))];
    }

    /// <summary>Host names from known_hosts: "host", "host,1.2.3.4" or "[host]:2222"; hashed and marker lines skipped.</summary>
    public static List<SshHost> ParseKnownHosts(string text)
    {
        var hosts = new List<SshHost>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or '@' or '|')
            {
                continue;
            }

            var names = line.Split([' ', '\t'], 2)[0];
            foreach (var entry in names.Split(','))
            {
                if (BracketedHost().Match(entry) is { Success: true } m)
                {
                    if (IsPlainHostName(m.Groups[1].Value))
                    {
                        hosts.Add(new SshHost(m.Groups[1].Value, null, null, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), KnownHostsSource));
                    }
                }
                else if (IsPlainHostName(entry))
                {
                    hosts.Add(new SshHost(entry, null, null, null, KnownHostsSource));
                }
            }
        }

        return hosts;
    }

    /// <summary>
    /// Letters, digits and . _ - % : only, not starting with '-' (ssh would read it as an option), so a name can go on a
    /// command line (and through Windows Terminal's own parsing) as it is.
    /// </summary>
    public static bool IsPlainHostName(string name) => name.Length is > 0 and <= 253 && name[0] != '-' && PlainName().IsMatch(name);

    // Includes are spliced in where they appear, as ssh does; relative paths are under ~/.ssh.
    private static string ReadAll(string path, string sshDirectory, string? home, int depth)
    {
        if (Read(path) is not { } text || depth > MaxIncludeDepth)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var (keyword, value) = SplitLine(raw);
            if (keyword is not null && keyword.Equals("include", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var pattern in value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                {
                    foreach (var file in Expand(pattern, sshDirectory, home))
                    {
                        lines.Add(ReadAll(file, sshDirectory, home, depth + 1));
                    }
                }

                continue;
            }

            lines.Add(raw);
        }

        return string.Join('\n', lines);
    }

    private static IEnumerable<string> Expand(string pattern, string sshDirectory, string? home)
    {
        if (pattern.StartsWith('~') && home is not null)
        {
            pattern = Path.Join(home, pattern[1..]);
        }

        var full = Path.IsPathRooted(pattern) ? pattern : Path.Combine(sshDirectory, pattern);
        var directory = Path.GetDirectoryName(full);
        var name = Path.GetFileName(full);
        if (directory is null || directory.IndexOfAny(['*', '?']) >= 0 || !Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return name.IndexOfAny(['*', '?']) >= 0 ? [.. Directory.EnumerateFiles(directory, name).Order(StringComparer.Ordinal)] : [full];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static (string? Keyword, string Value) SplitLine(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] == '#')
        {
            return (null, string.Empty);
        }

        var split = line.IndexOfAny([' ', '\t', '=']);
        if (split < 0)
        {
            return (line, string.Empty);
        }

        return (line[..split], line[(split + 1)..].TrimStart(' ', '\t', '=').Trim().Trim('"'));
    }

    private static string? Read(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length < 4 << 20 ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._%:-]+$")]
    private static partial Regex PlainName();

    [GeneratedRegex(@"^\[([^\]]+)\]:(\d{1,5})$")]
    private static partial Regex BracketedHost();
}
