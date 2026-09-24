using System.Text.RegularExpressions;

namespace Pickle.Core.Aliases;

/// <summary>Validation for names that become PowerShell functions (aliases, shims).</summary>
public static partial class AliasNames
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "begin", "break", "catch", "class", "clean", "configuration", "continue", "data", "define", "do", "dynamicparam",
        "else", "elseif", "end", "enum", "exit", "filter", "finally", "for", "foreach", "from", "function", "hidden", "if",
        "in", "inlinescript", "parallel", "param", "process", "return", "sequence", "static", "switch", "throw", "trap",
        "try", "until", "using", "var", "while", "workflow",
    };

    public static bool IsValid(string? name) => name is not null && NameRegex().IsMatch(name) && !Keywords.Contains(name);

    /// <summary>Throws <see cref="ArgumentException"/> with a user-facing message if the name can't be a command name.</summary>
    public static void Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Alias name is empty.");
        }

        if (Keywords.Contains(name))
        {
            throw new ArgumentException($"'{name}' is a PowerShell keyword and can't be an alias name.");
        }

        if (!NameRegex().IsMatch(name))
        {
            throw new ArgumentException(
                $"'{name}' isn't a valid alias name. Use letters, digits, '_', '-', '.' (max 64 chars, not starting with '-' or '.'), or just dots like '..'.");
        }
    }

    // Leading '.' would parse as dot-sourcing and a leading '-' as a parameter, so only all-dot names may start with '.'.
    [GeneratedRegex(@"^(?:\.{2,5}|(?![0-9]+$)[A-Za-z0-9_][A-Za-z0-9_.-]{0,63})$")]
    private static partial Regex NameRegex();
}
