using System.Management.Automation.Language;
using System.Text;

namespace Pickle.Wizards;

/// <summary>
/// Formats values as PowerShell command arguments. Anything that PowerShell would not pass through verbatim as a
/// bare word is single-quoted; inside single quotes only the (Unicode) single-quote characters are special.
/// </summary>
public static class PowerShellQuoting
{
    public static bool IsSingleQuote(char c) => c is '\'' or '‘' or '’' or '‚' or '‛';

    public static bool IsDash(char c) => c is '-' or '–' or '—' or '―';

    public static string QuoteLiteral(string value)
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

    /// <summary>Returns the value as-is when PowerShell passes it through unchanged, otherwise single-quoted.</summary>
    /// <param name="allowLeadingDash">True for flags and flag+value tokens; values starting with a dash are quoted so
    /// cmdlets don't take them as parameter names.</param>
    public static string FormatArgument(string value, bool allowLeadingDash = false) =>
        NeedsQuoting(value, allowLeadingDash) ? QuoteLiteral(value) : value;

    public static bool NeedsQuoting(string value, bool allowLeadingDash = false)
    {
        if (value.Length == 0 || value[0] == '@' || value.StartsWith("--%", StringComparison.Ordinal))
        {
            return true;
        }

        if (value[0] != '-' && IsDash(value[0]))
        {
            return true;
        }

        if (value[0] == '-' && !allowLeadingDash)
        {
            return true;
        }

        foreach (var c in value)
        {
            if (!IsSafeChar(c))
            {
                return true;
            }
        }

        // The character check is conservative; the tokenizer settles the rest (1kb/0x10/1e5 become numbers,
        // "-a.b" splits into two arguments, ...).
        return !ParsesAsSingleVerbatimArgument(value, allowLeadingDash);
    }

    /// <summary>True when <paramref name="text"/> is exactly one PowerShell command argument (used for raw values).</summary>
    public static bool IsSingleArgument(string text) => TryParseArguments(text, out var count) && count == 1;

    /// <summary>True when <paramref name="text"/> is zero or more command arguments and nothing else (no statements, pipes).</summary>
    public static bool IsArgumentList(string text) => TryParseArguments(text, out _);

    /// <summary>A PowerShell literal for a value placed inside PowerShell source (raw templates): number or quoted string.</summary>
    public static string ToExpressionLiteral(string value)
    {
        var trimmed = value.Trim();
        if (IsNumber(trimmed))
        {
            return trimmed;
        }

        if (IsNumberList(trimmed))
        {
            return string.Join(',', trimmed.Split(',', StringSplitOptions.TrimEntries));
        }

        return QuoteLiteral(value);
    }

    /// <summary>Inverse of <see cref="ToExpressionLiteral"/>.</summary>
    public static string FromExpressionLiteral(string literal)
    {
        var text = literal.Trim();
        if (text.Length >= 2 && IsSingleQuote(text[0]) && IsSingleQuote(text[^1]))
        {
            var sb = new StringBuilder(text.Length);
            for (var i = 1; i < text.Length - 1; i++)
            {
                sb.Append(text[i]);
                if (IsSingleQuote(text[i]) && i + 1 < text.Length - 1 && IsSingleQuote(text[i + 1]))
                {
                    i++;
                }
            }

            return sb.ToString();
        }

        return IsNumberList(text) ? string.Join(',', text.Split(',', StringSplitOptions.TrimEntries)) : text;
    }

    private static bool IsNumber(string text)
    {
        var digits = text.StartsWith('-') ? text[1..] : text;
        var parts = digits.Split('.');
        return parts.Length <= 2 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit));
    }

    private static bool IsNumberList(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length > 1 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit));
    }

    private static bool IsSafeChar(char c) =>
        char.IsAsciiLetterOrDigit(c)
        || (c > 127 && char.IsLetterOrDigit(c))
        || c is '_' or '.' or '/' or '\\' or ':' or '=' or '+' or '-' or '%' or '^' or '!' or '~' or '@';

    private static bool ParsesAsSingleVerbatimArgument(string value, bool allowLeadingDash)
    {
        if (ParseSingleCommand("x " + value) is not { } command || command.Redirections.Count > 0
            || command.CommandElements.Count != 2)
        {
            return false;
        }

        return command.CommandElements[1] switch
        {
            StringConstantExpressionAst s => s.StringConstantType == StringConstantType.BareWord && s.Value == value,
            ConstantExpressionAst c => c.Extent.Text == value && value.All(char.IsAsciiDigit),
            CommandParameterAst p => allowLeadingDash && p.Extent.Text == value,
            _ => false,
        };
    }

    private static bool TryParseArguments(string text, out int count)
    {
        count = 0;
        if (text.Contains('\n') || text.Contains('\r'))
        {
            return false;
        }

        if (ParseSingleCommand("x " + text) is not { } command)
        {
            return false;
        }

        // Adjacent elements (-a.b) count as one argument.
        var elements = command.CommandElements;
        for (var i = 1; i < elements.Count; i++)
        {
            if (i == 1 || elements[i].Extent.StartOffset != elements[i - 1].Extent.EndOffset)
            {
                count++;
            }
        }

        return true;
    }

    private static CommandAst? ParseSingleCommand(string input)
    {
        var ast = Parser.ParseInput(input, out _, out var errors);
        if (errors.Length > 0
            || ast.BeginBlock is not null || ast.ProcessBlock is not null || ast.ParamBlock is not null
            || ast.EndBlock is null || ast.EndBlock.Statements.Count != 1
            || ast.EndBlock.Statements[0] is not PipelineAst pipeline
            || pipeline.Background
            || pipeline.PipelineElements.Count != 1
            || pipeline.PipelineElements[0] is not CommandAst command
            || command.InvocationOperator != TokenKind.Unknown)
        {
            return null;
        }

        return command;
    }
}
