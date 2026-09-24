using System.Management.Automation.Language;
using Pickle.Abstractions;
using Pickle.Core.Contracts;
using Pickle.Core.Terminal;

namespace Pickle.Core.Syntax;

/// <summary>
/// PowerShell tokenizer → theme colors. Command names are checked against a background-loaded
/// <see cref="CommandCache"/> (unknown ones use <see cref="SyntaxColors.UnknownCommand"/>); parse errors are
/// underlined except "incomplete input" (the user is still typing). Inputs above
/// <see cref="EditorSettings.HighlightDebounceThreshold"/> are parsed off-thread while stale spans are shown.
/// </summary>
public sealed class SyntaxHighlighter : ISyntaxHighlighter, IRuntimeComponent, IDisposable
{
    private readonly PickleRuntime _runtime;
    private readonly object _lazyGate = new();
    private CommandCache? _commands;
    private Result? _last;
    private string? _lazyWanted;
    private string? _lazyCurrent;
    private Task? _lazyTask;
    private int _lazyCompleted;
    private IDisposable? _postExecuteHook;
    private IDisposable? _directoryHook;

    public SyntaxHighlighter(PickleRuntime runtime) => _runtime = runtime;

    internal enum Role : byte
    {
        Default,
        Command,
        UnknownCommand,
        Parameter,
        String,
        Number,
        Variable,
        Operator,
        Keyword,
        Comment,
        Type,
        Member,
    }

    internal CommandCache Commands => _commands ??= new CommandCache(_runtime);

    /// <summary>Changes when results for an unchanged input may differ (commands loaded, background parse finished).</summary>
    internal int Version => (_commands?.Version ?? 0) + Volatile.Read(ref _lazyCompleted);

    public void OnStarted()
    {
        _postExecuteHook = _runtime.Hooks.Register(HookKind.PostExecute, (_, _) =>
        {
            Commands.OnCommandFinished();
            return ValueTask.CompletedTask;
        });
        _directoryHook = _runtime.Hooks.Register(HookKind.DirectoryChanged, (_, _) =>
        {
            Commands.OnDirectoryChanged();
            return ValueTask.CompletedTask;
        });

        // The real interactive shell warms the cache right away; test runtimes only pay for it when they highlight.
        if (_runtime.Terminal is ConsoleTerminal { IsInteractive: true } && !_runtime.Options.Headless)
        {
            Commands.EnsureStarted();
        }
    }

    /// <summary>Starts loading known commands (if needed) and completes when they are available.</summary>
    internal Task WhenCommandsLoaded() => Commands.EnsureStarted();

    public IReadOnlyList<StyledSpan> Highlight(string input)
    {
        if (input.Length == 0)
        {
            return [];
        }

        var theme = _runtime.Themes.Current;
        var cwd = _runtime.Engine.CurrentDirectory;
        var version = Commands.Version;
        var last = _last;
        if (last is not null && last.Input == input && ReferenceEquals(last.Theme, theme) && last.Version == version && last.Cwd == cwd)
        {
            return last.Spans;
        }

        if (input.Length > Math.Max(256, _runtime.Config.Current.Editor.HighlightDebounceThreshold))
        {
            return HighlightLazily(input, theme, cwd, version, last);
        }

        var spans = Compute(input, theme, cwd);
        _last = new Result(input, spans, theme, version, cwd);
        return spans;
    }

    public void Dispose()
    {
        _postExecuteHook?.Dispose();
        _directoryHook?.Dispose();
        _commands?.Dispose();
    }

    internal IReadOnlyList<StyledSpan> Compute(string input, Theme theme, string cwd)
    {
        Parser.ParseInput(input, out var tokens, out var errors);
        var roles = new Role[input.Length];
        var defined = FunctionsDefinedIn(tokens);
        var sawCommand = false;
        foreach (var token in tokens)
        {
            Classify(input, token, roles, defined, cwd, ref sawCommand);
        }

        if (sawCommand && _commands?.IsReady != true)
        {
            Commands.EnsureStarted();
        }

        var errorMask = new bool[input.Length];
        foreach (var error in errors)
        {
            if (error.IncompleteInput)
            {
                continue;
            }

            var start = Math.Clamp(error.Extent.StartOffset, 0, input.Length);
            var end = Math.Clamp(Math.Max(error.Extent.EndOffset, start + 1), 0, input.Length);
            if (start == input.Length && input.Length > 0)
            {
                start = input.Length - 1;
            }

            for (var i = start; i < end; i++)
            {
                errorMask[i] = true;
            }
        }

        return ToSpans(roles, errorMask, theme);
    }

    private void Classify(string input, Token token, Role[] roles, HashSet<string>? defined, string cwd, ref bool sawCommand)
    {
        var role = RoleOf(token);
        if (role == Role.Command)
        {
            sawCommand = true;
            if (!IsKnownCommand(token, defined, cwd) && !IsTranslatedCommand(input, token, cwd))
            {
                role = Role.UnknownCommand;
            }
        }

        if (role != Role.Default)
        {
            Fill(roles, token.Extent.StartOffset, token.Extent.EndOffset, role);
        }

        if (token is StringExpandableToken { NestedTokens: { Count: > 0 } nested })
        {
            foreach (var inner in nested)
            {
                Classify(input, inner, roles, defined, cwd, ref sawCommand);
            }
        }
    }

    private static Role RoleOf(Token token)
    {
        var flags = token.TokenFlags;
        if (flags.HasFlag(TokenFlags.CommandName))
        {
            return Role.Command;
        }

        switch (token.Kind)
        {
            case TokenKind.Comment:
                return Role.Comment;
            case TokenKind.StringLiteral:
            case TokenKind.StringExpandable:
            case TokenKind.HereStringLiteral:
            case TokenKind.HereStringExpandable:
                return Role.String;
            case TokenKind.Variable:
            case TokenKind.SplattedVariable:
                return Role.Variable;
            case TokenKind.Parameter:
                return Role.Parameter;
            case TokenKind.Number:
                return Role.Number;
            case TokenKind.Generic:
            case TokenKind.EndOfInput:
            case TokenKind.NewLine:
            case TokenKind.LineContinuation:
                return Role.Default;
        }

        if (flags.HasFlag(TokenFlags.Keyword))
        {
            return Role.Keyword;
        }

        if (flags.HasFlag(TokenFlags.TypeName))
        {
            return Role.Type;
        }

        if (flags.HasFlag(TokenFlags.MemberName))
        {
            return Role.Member;
        }

        if ((flags & (TokenFlags.BinaryOperator | TokenFlags.UnaryOperator | TokenFlags.AssignmentOperator)) != 0)
        {
            return Role.Operator;
        }

        return token.Kind switch
        {
            TokenKind.Pipe or TokenKind.Redirection or TokenKind.RedirectInStd or TokenKind.Ampersand or TokenKind.AndAnd
                or TokenKind.OrOr or TokenKind.Semi or TokenKind.ColonColon or TokenKind.DotDot or TokenKind.Comma => Role.Operator,
            _ => Role.Default,
        };
    }

    private bool IsKnownCommand(Token token, HashSet<string>? defined, string cwd)
    {
        if (token is StringExpandableToken { NestedTokens.Count: > 0 })
        {
            return true;
        }

        var name = token is StringToken s ? s.Value : token.Text;
        if (defined is not null && defined.Contains(name))
        {
            return true;
        }

        return _commands is null || _commands.IsKnown(name, cwd);
    }

    /// <summary>`apt install x`, `sudo x`, `export A=1`, `A=1 cmd`: the rewriters replace the command word itself.</summary>
    private bool IsTranslatedCommand(string input, Token token, string cwd)
    {
        var start = token.Extent.StartOffset;
        if (start < 0 || start >= input.Length)
        {
            return false;
        }

        try
        {
            var outcome = _runtime.Translation.Translate(input[start..], cwd);
            return outcome.Changed && !outcome.Command.StartsWith(token.Text, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            // History changed while a lazy (background) highlight enumerated it.
            return false;
        }
    }

    private static HashSet<string>? FunctionsDefinedIn(Token[] tokens)
    {
        HashSet<string>? names = null;
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (tokens[i].Kind is TokenKind.Function or TokenKind.Filter or TokenKind.Workflow or TokenKind.Configuration)
            {
                var next = tokens[i + 1];
                if (next.Kind is TokenKind.Generic or TokenKind.Identifier)
                {
                    (names ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(next is StringToken s ? s.Value : next.Text);
                }
            }
        }

        return names;
    }

    private static void Fill(Role[] roles, int start, int end, Role role)
    {
        start = Math.Clamp(start, 0, roles.Length);
        end = Math.Clamp(end, start, roles.Length);
        roles.AsSpan(start, end - start).Fill(role);
    }

    private static IReadOnlyList<StyledSpan> ToSpans(Role[] roles, bool[] errors, Theme theme)
    {
        var spans = new List<StyledSpan>();
        var styles = new Dictionary<(Role, bool), string>();
        var start = 0;
        for (var i = 1; i <= roles.Length; i++)
        {
            if (i < roles.Length && roles[i] == roles[start] && errors[i] == errors[start])
            {
                continue;
            }

            var key = (roles[start], errors[start]);
            if (!styles.TryGetValue(key, out var style))
            {
                style = StyleFor(key.Item1, key.Item2, theme.Syntax);
                styles[key] = style;
            }

            if (style.Length > 0)
            {
                spans.Add(new StyledSpan(start, i - start, style));
            }

            start = i;
        }

        return spans;
    }

    private static string StyleFor(Role role, bool error, SyntaxColors colors)
    {
        var color = role switch
        {
            Role.Command => colors.Command,
            Role.UnknownCommand => colors.UnknownCommand,
            Role.Parameter => colors.Parameter,
            Role.String => colors.String,
            Role.Number => colors.Number,
            Role.Variable => colors.Variable,
            Role.Operator => colors.Operator,
            Role.Keyword => colors.Keyword,
            Role.Comment => colors.Comment,
            Role.Type => colors.Type,
            Role.Member => colors.Member,
            _ => colors.Default,
        };

        if (error && role == Role.Default)
        {
            color = colors.Error;
        }

        return Ansi.Style(color, underline: error);
    }

    private IReadOnlyList<StyledSpan> HighlightLazily(string input, Theme theme, string cwd, int version, Result? last)
    {
        lock (_lazyGate)
        {
            if (!string.Equals(input, _lazyCurrent, StringComparison.Ordinal))
            {
                _lazyWanted = input;
            }

            if (_lazyTask is null || _lazyTask.IsCompleted)
            {
                _lazyTask = Task.Run(() => RunLazy(theme, cwd, version));
            }
        }

        if (last is null)
        {
            return [];
        }

        // Keep the stale colors up to the first changed character; the rest is plain until the parse catches up.
        var common = 0;
        var max = Math.Min(last.Input.Length, input.Length);
        while (common < max && last.Input[common] == input[common])
        {
            common++;
        }

        var kept = new List<StyledSpan>();
        foreach (var span in last.Spans)
        {
            if (span.Start >= common)
            {
                break;
            }

            kept.Add(span.Start + span.Length <= common ? span : span with { Length = common - span.Start });
        }

        return kept;
    }

    private void RunLazy(Theme theme, string cwd, int version)
    {
        while (true)
        {
            string input;
            lock (_lazyGate)
            {
                if (_lazyWanted is null)
                {
                    _lazyCurrent = null;
                    return;
                }

                input = _lazyWanted;
                _lazyWanted = null;
                _lazyCurrent = input;
            }

            try
            {
                var spans = Compute(input, theme, cwd);
                _last = new Result(input, spans, theme, version, cwd);
                Interlocked.Increment(ref _lazyCompleted);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _runtime.Log.Debug("highlight", $"background highlight failed: {ex.Message}");
            }
        }
    }

    private sealed record Result(string Input, IReadOnlyList<StyledSpan> Spans, Theme Theme, int Version, string Cwd);
}
