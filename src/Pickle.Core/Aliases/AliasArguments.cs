using System.Collections;
using System.Management.Automation;
using System.Text.RegularExpressions;

namespace Pickle.Core.Aliases;

/// <summary>
/// Lets a parameterized alias take <c>NAME=value</c> arguments. Called from the generated function; only when some
/// argument names a placeholder is everything re-bound (named first, the rest in order), else binding is untouched.
/// </summary>
public sealed partial class AliasArguments
{
    private readonly IReadOnlyList<string> _names;
    private readonly Dictionary<string, object?> _values;
    private readonly IDictionary _defaults;

    private AliasArguments(IReadOnlyList<string> names, Dictionary<string, object?> values, IDictionary defaults, object?[] rest)
    {
        _names = names;
        _values = values;
        _defaults = defaults;
        Rest = rest;
    }

    public object?[] Rest { get; }

    public static AliasArguments? ByName(object[] names, IDictionary defaults, IDictionary bound, object?[]? args)
    {
        var placeholders = names.Select(n => n.ToString()!).ToList();
        var given = placeholders.Where(bound.Contains).Select(n => bound[n]).Concat(args ?? []).ToList();
        if (!given.Any(v => Named(v, placeholders) is not null))
        {
            return null;
        }

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var rest = new List<object?>();
        foreach (var value in given)
        {
            if (Named(value, placeholders) is { } named && values.TryAdd(named.Name, named.Value))
            {
                continue;
            }

            rest.Add(value);
        }

        foreach (var name in placeholders.Where(n => !values.ContainsKey(n) && rest.Count > 0))
        {
            values[name] = rest[0];
            rest.RemoveAt(0);
        }

        return new AliasArguments(placeholders, values, defaults, [.. rest]);
    }

    public void Apply(SessionState state, IDictionary bound)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(bound);
        foreach (var name in _names)
        {
            if (_values.TryGetValue(name, out var value))
            {
                state.PSVariable.Set(name, value);
                bound[name] = value;
            }
            else
            {
                bound.Remove(name);
                state.PSVariable.Set(name, _defaults.Contains(name) ? _defaults[name] : null);
            }
        }
    }

    private static (string Name, string Value)? Named(object? value, List<string> placeholders)
    {
        if (PSObject.AsPSObject(value).BaseObject is not string text || NamedRegex().Match(text) is not { Success: true } match)
        {
            return null;
        }

        var name = placeholders.FirstOrDefault(p => p.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
        return name is null ? null : (name, match.Groups[2].Value);
    }

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*)=(.*)$", RegexOptions.Singleline)]
    private static partial Regex NamedRegex();
}
