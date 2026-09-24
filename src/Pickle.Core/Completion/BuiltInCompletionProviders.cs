using System.Management.Automation.Language;
using Pickle.Abstractions;

namespace Pickle.Core.Completion;

/// <summary>A provider whose non-empty result replaces PowerShell's and every other provider's.</summary>
internal interface IExclusiveCompletionProvider : ICompletionProvider
{
}

/// <summary><c>pk &lt;Tab&gt;</c> / <c>pk help &lt;Tab&gt;</c> → registered `pk` subcommands (PowerShell would offer files).</summary>
internal sealed class PickleCommandCompletionProvider(ICommandRegistry commands) : IExclusiveCompletionProvider
{
    private static readonly string[] CommandNames = ["pk", "pickle", "Invoke-PickleCommand"];

    public string Name => "pickle.commands";

    public int Priority => 100;

    public ValueTask<CompletionSet?> GetCompletionsAsync(CompletionRequest request, CancellationToken cancellationToken)
    {
        if (FindSubcommandWord(request.Input, request.Cursor) is not { } word)
        {
            return ValueTask.FromResult<CompletionSet?>(null);
        }

        var items = commands.All
            .Select(c => new CompletionItem(c.Name, c.Name, CompletionKind.Command, c.Description))
            .Append(new CompletionItem("help", "help", CompletionKind.Command, "List Pickle commands"))
            .Where(i => i.CompletionText.StartsWith(word.Prefix, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(i => i.CompletionText, StringComparer.OrdinalIgnoreCase)
            .OrderBy(i => i.CompletionText, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ValueTask.FromResult<CompletionSet?>(items.Count == 0 ? null : new CompletionSet(word.Start, word.Length, items));
    }

    /// <summary>The subcommand argument under the cursor of a <c>pk</c> command (or of <c>pk help</c>), if any.</summary>
    internal static (int Start, int Length, string Prefix)? FindSubcommandWord(string input, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, input.Length);
        var command = CompletionSyntax.CommandAt(input, cursor);
        if (command is null || !CommandNames.Contains(command.GetCommandName(), StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var elements = command.CommandElements;
        if (cursor <= elements[0].Extent.EndOffset)
        {
            return null;
        }

        var position = elements.Count;
        CommandElementAst? current = null;
        for (var i = 1; i < elements.Count; i++)
        {
            var extent = elements[i].Extent;
            if (extent.StartOffset <= cursor && cursor <= extent.EndOffset)
            {
                position = i;
                current = elements[i];
                break;
            }

            if (extent.StartOffset > cursor)
            {
                position = i;
                break;
            }
        }

        var isSubcommand = position == 1
            || (position == 2 && elements[1] is StringConstantExpressionAst { Value: var first } && first.Equals("help", StringComparison.OrdinalIgnoreCase));
        if (!isSubcommand)
        {
            return null;
        }

        if (current is null)
        {
            return (cursor, 0, string.Empty);
        }

        if (current is not StringConstantExpressionAst { StringConstantType: StringConstantType.BareWord })
        {
            return null;
        }

        var start = current.Extent.StartOffset;
        return (start, current.Extent.EndOffset - start, input[start..cursor]);
    }
}

/// <summary>Pickle aliases (with their descriptions) at command-name position.</summary>
internal sealed class AliasCompletionProvider(IAliasRegistry aliases) : ICompletionProvider
{
    public string Name => "pickle.aliases";

    public int Priority => 10;

    public ValueTask<CompletionSet?> GetCompletionsAsync(CompletionRequest request, CancellationToken cancellationToken)
    {
        var all = aliases.All;
        if (all.Count == 0 || CompletionSyntax.CommandNameTokenAt(request.Input, request.Cursor) is not { } token)
        {
            return ValueTask.FromResult<CompletionSet?>(null);
        }

        var prefix = request.Input[token.Start..Math.Clamp(request.Cursor, token.Start, request.Input.Length)];
        if (prefix.Length == 0)
        {
            return ValueTask.FromResult<CompletionSet?>(null);
        }

        var items = all
            .Where(a => a.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => new CompletionItem(a.Name, a.Name, CompletionKind.Alias, a.Description ?? Summarize(a.Body)))
            .ToList();
        return ValueTask.FromResult<CompletionSet?>(items.Count == 0 ? null : new CompletionSet(token.Start, token.Length, items));
    }

    private static string Summarize(string body)
    {
        var line = body.AsSpan().Trim();
        var newline = line.IndexOfAny('\r', '\n');
        return "→ " + (newline < 0 ? line : line[..newline]).ToString();
    }
}

internal static class CompletionSyntax
{
    /// <summary>The innermost command whose extent contains the cursor, or that the cursor follows after blanks.</summary>
    public static CommandAst? CommandAt(string input, int cursor)
    {
        var ast = Parser.ParseInput(input, out _, out _);
        CommandAst? best = null;
        foreach (var node in ast.FindAll(a => a is CommandAst, searchNestedScriptBlocks: true))
        {
            var command = (CommandAst)node;
            var extent = command.Extent;
            if (extent.StartOffset > cursor || (extent.EndOffset < cursor && !IsBlank(input, extent.EndOffset, cursor)))
            {
                continue;
            }

            if (best is null || extent.StartOffset >= best.Extent.StartOffset)
            {
                best = command;
            }
        }

        return best;
    }

    /// <summary>The command-name token that the cursor is in or at the end of.</summary>
    public static (int Start, int Length)? CommandNameTokenAt(string input, int cursor)
    {
        if (cursor <= 0 || cursor > input.Length)
        {
            return null;
        }

        Parser.ParseInput(input, out var tokens, out _);
        foreach (var token in tokens)
        {
            var extent = token.Extent;
            if (extent.StartOffset < cursor && cursor <= extent.EndOffset)
            {
                return (token.TokenFlags & TokenFlags.CommandName) != 0
                    ? (extent.StartOffset, extent.EndOffset - extent.StartOffset)
                    : null;
            }
        }

        return null;
    }

    private static bool IsBlank(string input, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            if (input[i] is not (' ' or '\t'))
            {
                return false;
            }
        }

        return true;
    }
}
