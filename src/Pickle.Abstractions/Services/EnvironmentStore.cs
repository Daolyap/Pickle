using System.Text.RegularExpressions;

namespace Pickle.Abstractions.Services;

public enum EnvironmentScope
{
    /// <summary>Set by Pickle for every session it starts (config.json); needs no privileges and works on every OS.</summary>
    Pickle,

    /// <summary>The current user's persistent variables (Windows: HKCU\Environment).</summary>
    User,

    /// <summary>All users' variables (Windows: HKLM, Linux: /etc/environment); needs administrator or root to change.</summary>
    Machine,

    /// <summary>What this very process sees (read only).</summary>
    Process,
}

public sealed record EnvironmentVariable(string Name, string Value, EnvironmentScope Scope)
{
    /// <summary>Windows REG_EXPAND_SZ: <c>%SystemRoot%</c> style references are expanded when read.</summary>
    public bool Expandable { get; init; }
}

/// <summary>Persistent environment variables per scope, including <c>PATH</c> (see <see cref="PathList"/>).</summary>
public interface IEnvironmentStore
{
    IReadOnlyList<EnvironmentScope> Scopes { get; }

    bool NeedsPrivileges(EnvironmentScope scope);

    Task<IReadOnlyList<EnvironmentVariable>> ListAsync(EnvironmentScope scope, CancellationToken cancellationToken = default);

    /// <summary>Sets <paramref name="name"/> (null <paramref name="value"/> removes it).</summary>
    Task<ServiceOperationResult> SetAsync(EnvironmentScope scope, string name, string? value, CancellationToken cancellationToken = default);

    /// <summary>
    /// The PATH entries of a scope: for <see cref="EnvironmentScope.Pickle"/> the folders Pickle prepends, for
    /// <see cref="EnvironmentScope.Process"/> the effective PATH, otherwise the persistent value, unexpanded.
    /// </summary>
    Task<IReadOnlyList<string>> GetPathAsync(EnvironmentScope scope, CancellationToken cancellationToken = default);

    Task<ServiceOperationResult> SetPathAsync(EnvironmentScope scope, IReadOnlyList<string> entries, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Pickle and Process scopes (config.json and the live process) shared by every OS; derived stores add what the
/// operating system persists (<see cref="EnvironmentScope.User"/>, <see cref="EnvironmentScope.Machine"/>).
/// </summary>
public abstract class EnvironmentStoreBase(IConfigStore config) : IEnvironmentStore
{
    public abstract IReadOnlyList<EnvironmentScope> Scopes { get; }

    public abstract bool NeedsPrivileges(EnvironmentScope scope);

    /// <summary>The name PATH has in this OS's persistent store ("Path" on Windows).</summary>
    protected virtual string PathVariable => "PATH";

    protected abstract Task<IReadOnlyList<EnvironmentVariable>> ListPersistentAsync(EnvironmentScope scope, CancellationToken cancellationToken);

    protected abstract Task<ServiceOperationResult> SetPersistentAsync(EnvironmentScope scope, string name, string? value, CancellationToken cancellationToken);

    public Task<IReadOnlyList<EnvironmentVariable>> ListAsync(EnvironmentScope scope, CancellationToken cancellationToken = default) => scope switch
    {
        EnvironmentScope.Pickle => Task.FromResult<IReadOnlyList<EnvironmentVariable>>(
            [.. config.Current.Shell.Environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Select(e => new EnvironmentVariable(e.Key, e.Value, scope))]),
        EnvironmentScope.Process => Task.FromResult<IReadOnlyList<EnvironmentVariable>>(
            [.. Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Select(e => new EnvironmentVariable((string)e.Key, (string?)e.Value ?? string.Empty, scope))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)]),
        _ => ListPersistentAsync(scope, cancellationToken),
    };

    public Task<ServiceOperationResult> SetAsync(EnvironmentScope scope, string name, string? value, CancellationToken cancellationToken = default)
    {
        EnvironmentRules.Validate(name, value, machineScope: scope == EnvironmentScope.Machine);
        switch (scope)
        {
            case EnvironmentScope.Pickle:
                config.Update(c =>
                {
                    if (value is null)
                    {
                        c.Shell.Environment.Remove(name);
                    }
                    else
                    {
                        c.Shell.Environment[name] = value;
                    }
                });
                return Task.FromResult(new ServiceOperationResult(true, value is null ? $"{name} removed from Pickle's environment." : $"{name} saved. Pickle sets it at every start."));
            case EnvironmentScope.Process:
                return Task.FromResult(new ServiceOperationResult(false, "The live process's variables are read-only here; set it with $env:NAME in the shell."));
            default:
                return SetPersistentAsync(scope, name, value, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<string>> GetPathAsync(EnvironmentScope scope, CancellationToken cancellationToken = default)
    {
        switch (scope)
        {
            case EnvironmentScope.Pickle:
                return [.. config.Current.Shell.PathPrepend];
            case EnvironmentScope.Process:
                return PathList.Parse(Environment.GetEnvironmentVariable("PATH")).Items;
            default:
                var variables = await ListPersistentAsync(scope, cancellationToken).ConfigureAwait(false);
                var path = variables.FirstOrDefault(v => string.Equals(v.Name, PathVariable, StringComparison.OrdinalIgnoreCase));
                return PathList.Parse(path?.Value).Items;
        }
    }

    public Task<ServiceOperationResult> SetPathAsync(EnvironmentScope scope, IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
    {
        var list = new PathList([]);
        foreach (var entry in entries)
        {
            list.Add(entry);
        }

        switch (scope)
        {
            case EnvironmentScope.Pickle:
                config.Update(c => c.Shell.PathPrepend = [.. list.Items]);
                return Task.FromResult(new ServiceOperationResult(true, "Saved. Pickle puts these first on PATH at every start."));
            case EnvironmentScope.Process:
                return Task.FromResult(new ServiceOperationResult(false, "The live PATH is read-only here."));
            default:
                return SetPersistentAsync(scope, PathVariable, list.Serialize(), cancellationToken);
        }
    }
}

/// <summary>Rules every writer of environment variables applies, in the editor and in the elevated helpers.</summary>
public static partial class EnvironmentRules
{
    public const int MaxValueLength = 32_000;

    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "ComSpec", "windir", "SystemRoot", "SystemDrive", "OS", "PATHEXT", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "TEMP", "TMP",
        "LD_PRELOAD", "LD_LIBRARY_PATH", "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH",
    };

    public static bool IsValidName(string name) => Name().IsMatch(name);

    /// <summary>Variables that can hijack every program on the machine are not editable here.</summary>
    public static bool IsProtected(string name) => Protected.Contains(name);

    public static void Validate(string name, string? value, bool machineScope)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException($"'{name}' is not a valid variable name (letters, digits and _; not starting with a digit).");
        }

        if (machineScope && IsProtected(name))
        {
            throw new ArgumentException($"{name} is protected and can't be changed for all users from here.");
        }

        if (value is not null && (value.Length > MaxValueLength || value.Any(c => char.IsControl(c))))
        {
            throw new ArgumentException($"The value of {name} is too long or contains control characters.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,254}$")]
    private static partial Regex Name();
}

/// <summary>A <c>PATH</c>-style value as an editable list: reorder, add, remove, and flag what is missing or repeated.</summary>
public sealed class PathList
{
    private readonly List<string> _items;

    public PathList(IEnumerable<string> items) => _items = [.. items];

    public static char Separator => OperatingSystem.IsWindows() ? ';' : ':';

    public IReadOnlyList<string> Items => _items;

    public static PathList Parse(string? value, char? separator = null) =>
        new((value ?? string.Empty).Split(separator ?? Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public string Serialize(char? separator = null) => string.Join(separator ?? Separator, _items);

    public void Add(string entry, int? index = null)
    {
        if (string.IsNullOrWhiteSpace(entry) || entry.Any(char.IsControl) || entry.Contains(Separator, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{entry}' can't be a PATH entry (empty, control characters or the separator '{Separator}').");
        }

        _items.Insert(Math.Clamp(index ?? _items.Count, 0, _items.Count), entry.Trim().Trim('"'));
    }

    public void RemoveAt(int index) => _items.RemoveAt(index);

    public bool Move(int index, int delta)
    {
        var target = index + delta;
        if (index < 0 || index >= _items.Count || target < 0 || target >= _items.Count)
        {
            return false;
        }

        (_items[index], _items[target]) = (_items[target], _items[index]);
        return true;
    }

    /// <summary>The entry's problem, if any: "missing" (no such folder), "duplicate" (earlier entry is the same folder) or "relative".</summary>
    public string? Problem(int index, Func<string, bool> directoryExists, Func<string, string> expand)
    {
        var entry = _items[index];
        var expanded = expand(entry);
        if (!Path.IsPathFullyQualified(expanded))
        {
            return "relative";
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var i = 0; i < index; i++)
        {
            if (string.Equals(expand(_items[i]).TrimEnd('\\', '/'), expanded.TrimEnd('\\', '/'), comparison))
            {
                return "duplicate";
            }
        }

        return directoryExists(expanded) ? null : "missing";
    }
}

/// <summary>A <c>.env</c> file (<c>KEY=value</c>, <c>export KEY=value</c>, # comments, quotes) edited without disturbing the lines it does not touch.</summary>
public sealed partial class DotEnvDocument
{
    private readonly List<string> _lines = [];

    public static DotEnvDocument Parse(string text)
    {
        var document = new DotEnvDocument();
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        if (text.Length > 0)
        {
            document._lines.AddRange(normalized.Split('\n'));
        }

        return document;
    }

    public IReadOnlyList<(string Name, string Value)> Variables
    {
        get
        {
            var list = new List<(string, string)>();
            foreach (var line in _lines)
            {
                if (TryParse(line) is { } pair)
                {
                    list.Add(pair);
                }
            }

            return list;
        }
    }

    public void Set(string name, string value)
    {
        if (!EnvironmentRules.IsValidName(name))
        {
            throw new ArgumentException($"'{name}' is not a valid variable name.");
        }

        if (value.Any(c => c is '\n' or '\r' or '\0'))
        {
            throw new ArgumentException("A .env value can't contain line breaks.");
        }

        var formatted = $"{name}={Quote(value)}";
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (TryParse(_lines[i]) is { } pair && pair.Name == name)
            {
                _lines[i] = (_lines[i].TrimStart().StartsWith("export ", StringComparison.Ordinal) ? "export " : string.Empty) + formatted;
                return;
            }
        }

        _lines.Add(formatted);
    }

    public bool Remove(string name)
    {
        var removed = false;
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (TryParse(_lines[i]) is { } pair && pair.Name == name)
            {
                _lines.RemoveAt(i);
                removed = true;
            }
        }

        return removed;
    }

    public string Serialize() => _lines.Count == 0 ? string.Empty : string.Join('\n', _lines) + "\n";

    public static (string Name, string Value)? TryParse(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text[0] == '#')
        {
            return null;
        }

        if (text.StartsWith("export ", StringComparison.Ordinal))
        {
            text = text[7..].TrimStart();
        }

        var match = Assignment().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[2].Value.Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            var quote = value[0];
            value = value[1..^1];
            if (quote == '"')
            {
                value = value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
            }
        }
        else if (value.IndexOf(" #", StringComparison.Ordinal) is var comment and >= 0)
        {
            value = value[..comment].TrimEnd();
        }

        return (match.Groups[1].Value, value);
    }

    private static string Quote(string value) =>
        value.Length == 0 || value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '/' or ':' or '-' or ',' or '@' or '+' or '=')
            ? value
            : "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$")]
    private static partial Regex Assignment();
}
