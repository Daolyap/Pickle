using System.Text;

namespace Pickle.Abstractions;

/// <summary>
/// Builds PowerShell command lines for <see cref="PanelResult"/>s and generated scripts. PowerShell treats ‘ ’ ‚ ‛ as
/// single quotes too, so every one of them is doubled inside a quoted string; never hand-roll <c>Replace("'", "''")</c>.
/// </summary>
public static class PowerShellQuote
{
    public static bool IsSingleQuote(char c) => c is '\'' or '‘' or '’' or '‚' or '‛';

    /// <summary>Always single-quoted: <c>it's</c> becomes <c>'it''s'</c>.</summary>
    public static string Single(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var c in value)
        {
            if (IsSingleQuote(c))
            {
                sb.Append(c);
            }

            sb.Append(c);
        }

        return sb.Append('\'').ToString();
    }

    /// <summary>Plain words stay bare (<c>nginx:1.27</c>), anything else is single-quoted.</summary>
    public static string Word(string value) => IsPlain(value) ? value : Single(value);

    /// <summary><c>program arg arg…</c> with every argument quoted as needed. Flags starting with <c>-</c> stay bare; values never are.</summary>
    public static string Command(string program, params IEnumerable<string> arguments) =>
        string.Join(' ', new[] { Word(program) }.Concat(arguments.Select(a => a.StartsWith('-') && IsPlain(a) ? a : Word(a))));

    private static bool IsPlain(string value)
    {
        if (value.Length == 0 || value[0] is '@' or '-' or '~' or '#' or '%' or '=')
        {
            return value.Length > 1 && value[0] == '-' && value.All(IsPlainChar);
        }

        return value.All(IsPlainChar);
    }

    private static bool IsPlainChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/' or ':' or '+' or '=' or '@' or ',' or '%';
}
