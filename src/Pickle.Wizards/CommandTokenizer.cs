using System.Management.Automation.Language;

namespace Pickle.Wizards;

/// <summary>
/// One argument of a parsed command. <see cref="Value"/> is the literal value PowerShell would pass (quotes removed)
/// when <see cref="IsLiteral"/>; otherwise the element is an expression (<c>$x</c>, <c>@{...}</c>) and only
/// <see cref="Source"/> (the original text) is meaningful.
/// </summary>
public sealed record CommandToken(string Value, bool IsLiteral, string Source, bool IsRedirection = false);

/// <summary>A command found in an input line, with the span it occupies (for replacing it in place).</summary>
public sealed record ParsedCommandLine(string Name, IReadOnlyList<CommandToken> Arguments, int Start, int Length);

public static class CommandTokenizer
{
    /// <summary>Every command in the line (pipelines, statements), in source order.</summary>
    public static IReadOnlyList<ParsedCommandLine> ParseCommands(string input)
    {
        var ast = Parser.ParseInput(input, out _, out _);
        return
        [
            .. ast.FindAll(a => a is CommandAst, searchNestedScriptBlocks: true)
                .Cast<CommandAst>()
                .OrderBy(c => c.Extent.StartOffset)
                .Select(c => Convert(input, c)),
        ];
    }

    /// <summary>The first command in the line, or null for an empty/expression-only line.</summary>
    public static ParsedCommandLine? ParseFirst(string input) => ParseCommands(input).FirstOrDefault();

    private static ParsedCommandLine Convert(string input, CommandAst command)
    {
        var elements = command.CommandElements;
        var name = elements.Count > 0 ? LiteralValue(elements[0]) ?? elements[0].Extent.Text : string.Empty;

        var parts = new List<(int Start, int End, string? Value, bool Redirection)>();
        for (var i = 1; i < elements.Count; i++)
        {
            parts.Add((elements[i].Extent.StartOffset, elements[i].Extent.EndOffset, LiteralValue(elements[i]), false));
        }

        foreach (var redirection in command.Redirections)
        {
            parts.Add((redirection.Extent.StartOffset, redirection.Extent.EndOffset, null, true));
        }

        parts.Sort((a, b) => a.Start.CompareTo(b.Start));

        // PowerShell splits "-ofile.txt" into "-ofile" + ".txt" but they are one argument to the program.
        var tokens = new List<CommandToken>();
        for (var i = 0; i < parts.Count; i++)
        {
            var (start, end, value, redirection) = parts[i];
            var literal = value is not null;
            var text = value ?? string.Empty;
            while (!redirection && i + 1 < parts.Count && !parts[i + 1].Redirection && parts[i + 1].Start == end)
            {
                i++;
                literal &= parts[i].Value is not null;
                text += parts[i].Value;
                end = parts[i].End;
            }

            var source = input[start..end];
            tokens.Add(new CommandToken(literal ? text : source, literal, source, redirection));
        }

        var commandStart = command.Extent.StartOffset;
        return new ParsedCommandLine(name, tokens, commandStart, command.Extent.EndOffset - commandStart);
    }

    private static string? LiteralValue(CommandElementAst element) => element switch
    {
        StringConstantExpressionAst s => s.Value,
        ExpandableStringExpressionAst { NestedExpressions.Count: 0 } x => x.Value,
        ConstantExpressionAst c => c.Extent.Text,
        CommandParameterAst p when p.Argument is null => "-" + p.ParameterName,
        CommandParameterAst p => LiteralValue(p.Argument) is { } arg ? "-" + p.ParameterName + ":" + arg : null,
        _ => null,
    };
}
