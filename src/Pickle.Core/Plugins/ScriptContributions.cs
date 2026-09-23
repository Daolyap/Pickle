using System.Collections;
using System.Management.Automation;
using System.Text.RegularExpressions;
using Pickle.Abstractions;
using Pickle.Core.Commands;

namespace Pickle.Core.Plugins;

/// <summary>
/// Invokes a plugin's scriptblock in the main runspace (in the scriptblock's own module scope) with extra
/// variables such as <c>$PickleEvent</c>. Safe from any thread; see <see cref="MainRunspace"/>.
/// </summary>
public static class ScriptInvoker
{
    private const string Script = """
        param($__pickleBlock, $__pickleVariables, $__pickleArgs)
        $__pickleVars = [System.Collections.Generic.List[psvariable]]::new()
        foreach ($__pickleKey in $__pickleVariables.Keys) { $__pickleVars.Add([psvariable]::new($__pickleKey, $__pickleVariables[$__pickleKey])) }
        $__pickleBlock.InvokeWithContext($null, $__pickleVars, $__pickleArgs)
        """;

    public static Task<ShellResult> InvokeAsync(
        PickleRuntime runtime,
        ScriptBlock block,
        IReadOnlyDictionary<string, object?>? variables,
        object?[] args,
        CancellationToken cancellationToken = default)
    {
        var table = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in variables ?? new Dictionary<string, object?>())
        {
            table[key] = value;
        }

        return MainRunspace.InvokeAsync(
            runtime,
            Script,
            new Dictionary<string, object?> { ["__pickleBlock"] = block, ["__pickleVariables"] = table, ["__pickleArgs"] = args },
            cancellationToken);
    }

    public static string? PropertyString(PSObject item, string name) =>
        item.Properties[name]?.Value is { } value ? LanguagePrimitives.ConvertTo<string>(value) : null;

    internal static void LogErrors(PickleRuntime runtime, string what, ShellResult result)
    {
        foreach (var error in result.Errors)
        {
            runtime.Log.Warn("plugins", $"{what}: {error}");
        }
    }
}

/// <summary>A prompt segment rendered by a scriptblock (<c>Register-PicklePromptSegment</c>).</summary>
public sealed class ScriptPromptSegment(PickleRuntime runtime, string type, ScriptBlock block) : IPromptSegment
{
    public string Type => type;

    public async ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken)
    {
        var result = await ScriptInvoker.InvokeAsync(
            runtime,
            block,
            new Dictionary<string, object?> { ["PickleContext"] = context, ["PickleSegment"] = style },
            [context],
            cancellationToken).ConfigureAwait(false);
        ScriptInvoker.LogErrors(runtime, $"prompt segment '{type}'", result);

        var items = result.Output.Where(o => o is not null).ToList();
        if (items.Count == 0)
        {
            return null;
        }

        if (items.Count == 1 && items[0].BaseObject is not string && items[0].Properties["Text"] is not null)
        {
            var text = ScriptInvoker.PropertyString(items[0], "Text");
            return string.IsNullOrEmpty(text)
                ? null
                : new PromptSegmentOutput(text, ScriptInvoker.PropertyString(items[0], "Foreground"), ScriptInvoker.PropertyString(items[0], "Background"));
        }

        var joined = string.Join(' ', items.Select(i => i.ToString()));
        return joined.Length == 0 ? null : new PromptSegmentOutput(joined);
    }
}

/// <summary>Argument completion for specific commands, backed by a scriptblock (<c>Register-PickleCompletion</c>).</summary>
public sealed class ScriptCompletionProvider(PickleRuntime runtime, string name, IReadOnlyList<string> commandNames, ScriptBlock block) : ICompletionProvider
{
    public string Name => name;

    public int Priority => 100;

    public IReadOnlyList<string> CommandNames => commandNames;

    public async ValueTask<CompletionSet?> GetCompletionsAsync(CompletionRequest request, CancellationToken cancellationToken)
    {
        if (Analyze(request.Input, request.Cursor) is not { } target || !Matches(target.Command))
        {
            return null;
        }

        var word = request.Input[target.WordStart..request.Cursor];
        var result = await ScriptInvoker.InvokeAsync(
            runtime,
            block,
            new Dictionary<string, object?> { ["PickleCompletion"] = request },
            [word, request.Input, request.Cursor],
            cancellationToken).ConfigureAwait(false);
        ScriptInvoker.LogErrors(runtime, $"completion '{name}'", result);

        var items = new List<CompletionItem>();
        foreach (var output in result.Output.Where(o => o is not null))
        {
            if (output.BaseObject is string s)
            {
                items.Add(new CompletionItem(s, s, CompletionKind.ParameterValue));
                continue;
            }

            var completion = ScriptInvoker.PropertyString(output, "CompletionText") ?? output.ToString();
            items.Add(new CompletionItem(
                completion,
                ScriptInvoker.PropertyString(output, "ListText") ?? completion,
                CompletionKind.ParameterValue,
                ScriptInvoker.PropertyString(output, "Description") ?? ScriptInvoker.PropertyString(output, "ToolTip")));
        }

        return items.Count == 0 ? null : new CompletionSet(target.WordStart, request.Cursor - target.WordStart, items);
    }

    private bool Matches(string command)
    {
        var bare = command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? command[..^4] : command;
        bare = Path.GetFileName(bare);
        return commandNames.Any(n => WildcardPattern.Get(n, WildcardOptions.IgnoreCase).IsMatch(bare));
    }

    /// <summary>The command of the pipeline element under the cursor and where the word being completed starts.</summary>
    internal static (string Command, int WordStart)? Analyze(string input, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, input.Length);
        var segmentStart = 0;
        var quote = '\0';
        for (var i = 0; i < cursor; i++)
        {
            var c = input[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c is '|' or ';' or '\n' or '{' or '(' || (c == '&' && i + 1 < input.Length && input[i + 1] == '&'))
            {
                segmentStart = i + 1;
            }
        }

        var segment = input[segmentStart..cursor];
        var trimmed = segment.TrimStart();
        if (trimmed.StartsWith('&') || trimmed.StartsWith('.'))
        {
            trimmed = trimmed[1..].TrimStart();
        }

        var end = 0;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]))
        {
            end++;
        }

        if (end == trimmed.Length)
        {
            return null;
        }

        var command = trimmed[..end].Trim('"', '\'');
        var wordStart = cursor;
        while (wordStart > segmentStart && !char.IsWhiteSpace(input[wordStart - 1]))
        {
            wordStart--;
        }

        return command.Length == 0 ? null : (command, wordStart);
    }
}

/// <summary>A <c>pk</c> subcommand implemented by a scriptblock (<c>Register-PickleCommand</c>).</summary>
public sealed class ScriptPickleCommand(PickleRuntime runtime, string name, string description, string usage, ScriptBlock block) : IPickleCommand
{
    public string Name => name;

    public string Description => description;

    public string Usage => usage;

    public async ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await ScriptInvoker.InvokeAsync(
            runtime,
            block,
            new Dictionary<string, object?> { ["PickleCommand"] = context },
            [.. args],
            cancellationToken).ConfigureAwait(false);
        foreach (var output in result.Output)
        {
            context.WriteObject(output);
        }

        foreach (var error in result.Errors)
        {
            context.WriteError(error.ToString());
        }

        return result.HadErrors ? 1 : 0;
    }
}

/// <summary>A regex input rewriter (<c>Register-PickleTranslation -Pattern -Replacement</c>).</summary>
public sealed class RegexRewriter : IInputRewriter
{
    private readonly Regex _regex;
    private readonly string _replacement;

    public RegexRewriter(string name, string pattern, string replacement, int order = 500)
    {
        Name = name;
        Order = order;
        _regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        _replacement = replacement;
    }

    public string Name { get; }

    public int Order { get; }

    public RewriteResult? Rewrite(string input, RewriteContext context)
    {
        try
        {
            var rewritten = _regex.Replace(input, _replacement);
            return rewritten == input ? null : new RewriteResult(rewritten, Name);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}
