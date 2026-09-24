using System.Text.RegularExpressions;

namespace Pickle.Core.History;

/// <summary>Heuristics for command lines that carry credentials; such lines stay in memory and are never written to disk.</summary>
public static partial class SecretDetector
{
    public static bool ContainsSecret(string commandLine) =>
        !string.IsNullOrEmpty(commandLine) && (SecretParameter().IsMatch(commandLine) || SecretToken().IsMatch(commandLine));

    // A secret-looking parameter (-Password, -ApiKey, --token=...) followed by a literal value. Values that start
    // with $, (, @, { or - are variables/expressions/other parameters, not secrets typed in clear. A bare -Key whose
    // value is a key chord (Set-PSReadLineKeyHandler -Key Ctrl+r) is not a secret either.
    [GeneratedRegex(
        """
        (?<![\w-])--?
        (?:
            (?:[\w-]*?(?:password|passwd|passphrase|token|secret|api[-_]?key|access[-_]?key|private[-_]?key)[\w-]*|pwd)
            (?:\s*[:=]\s*|\s+)(?![-$(@{])\S
          | key(?:\s*[:=]\s*|\s+)(?![-$(@{])
            (?!['"]?(?:ctrl|alt|shift|meta|tab|enter|escape|esc|spacebar|space|backspace|delete|insert|home|end|pageup|pagedown|(?:up|down|left|right)arrow|f\d{1,2})\b)\S
        )
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex SecretParameter();

    [GeneratedRegex(
        """
        (?ix:
            ConvertTo-SecureString\b.*?-AsPlainText
          | (?<![\w$])(?:password|pwd)\s*=\s*[^;'"\s]
          | \$\w*(?:password|passwd|secret|token|apikey|api_key)\w*\s*=\s*['"][^'"]
          | Authorization\s*[:=]\s*['"]?\s*(?:Bearer|Basic|Token)\s+\S
          | \bBearer\s+[\w\-.~+/]{16,}
          | ://[^/\s:@]+:[^/\s@]+@
          | \bcurl(?:\.exe)?\b.*?\s(?:-u|--user)\s*[^\s:$]+:[^\s$]
          | -----BEGIN\s(?:[A-Z]+\s)*PRIVATE\sKEY-----
        )
        | \b(?:AKIA|ASIA)[0-9A-Z]{16}\b
        | \bgh[pousr]_[A-Za-z0-9]{20,}
        | \bgithub_pat_[A-Za-z0-9_]{20,}
        | \bxox[abposr]-[A-Za-z0-9-]{10,}
        | \bsk-(?:ant-|proj-)?[A-Za-z0-9_-]{20,}
        """,
        RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex SecretToken();
}
