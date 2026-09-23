using Pickle.Abstractions;
using Pickle.Core.Contracts;

namespace Pickle.Core.Prompt;

/// <summary>
/// FOUNDATION PLACEHOLDER (workstream W3 replaces this file): "cwd ❯ ". W3 renders the theme's segments
/// (cwd, git, status, duration, ...), right prompt, separators, and the transient prompt.
/// </summary>
public sealed class PromptEngine : IPromptRenderer
{
    private readonly PickleRuntime _runtime;

    public PromptEngine(PickleRuntime runtime) => _runtime = runtime;

    public PromptRender Render(PromptContext context)
    {
        var theme = _runtime.Themes.Current;
        var charColor = context.LastCommandSucceeded ? theme.Prompt.PromptCharColor : theme.Prompt.PromptCharErrorColor;
        var left = Ansi.Colorize(context.Cwd, theme.Ui.Info) + " " + Ansi.Colorize(theme.Prompt.PromptChar, charColor) + " ";
        return new PromptRender(left, null, theme.Prompt.ContinuationPrompt);
    }

    public string RenderTransient(PromptContext context)
    {
        var theme = _runtime.Themes.Current;
        return Ansi.Colorize(theme.Prompt.PromptChar, theme.Prompt.PromptCharColor) + " ";
    }

    public void Prefetch(PromptContext context)
    {
    }
}
