using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Core.Contracts;
using Pickle.Core.Prompt;
using Pickle.Testing;

namespace Pickle.Core.Tests.Prompt;

public class PromptRenderTests
{
    public static readonly string[] BuiltInThemes = ["classic", "minimal", "mono", "pickle", "powerline"];

    public static TheoryData<string, int> ThemesAndWidths()
    {
        var data = new TheoryData<string, int>();
        foreach (var theme in BuiltInThemes)
        {
            data.Add(theme, 80);
            data.Add(theme, 40);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ThemesAndWidths))]
    public void BuiltInThemePrompt(string theme, int width)
    {
        var preview = new ThemePreview(windowsPaths: false);
        var failed = preview.Render(LoadTheme(theme), width);
        var succeeded = preview.Render(LoadTheme(theme), width, lastCommandSucceeded: true);
        Snapshot.Match(
            "after a failed command:\n" + Screen(failed, width) + "\n\nafter a successful command:\n" + Screen(succeeded, width),
            $"{theme}-{width}");
    }

    [Fact]
    public void ClassicThemeLooksLikePowerShellOnWindowsPaths()
    {
        var render = new ThemePreview(windowsPaths: true).Render(LoadTheme("classic"), 80, lastCommandSucceeded: true);
        Assert.Equal(@"PS C:\Users\pickle\src\pickle\src\Pickle.Core\Prompt> ", TextWidth.StripAnsi(render.Left).Replace("[ADMIN] ", string.Empty, StringComparison.Ordinal));
        Assert.Null(render.Right);
        Assert.Equal(">> ", TextWidth.StripAnsi(render.Continuation));
    }

    [Fact]
    public void TransientPrompts()
    {
        var preview = new ThemePreview(windowsPaths: false);
        var lines = BuiltInThemes.Select(t => t.PadRight(10) + preview.RenderTransient(LoadTheme(t), 80) + "Get-Date");
        var vt = new VirtualTerminal(80, BuiltInThemes.Length + 1);
        vt.Write(string.Join("\n", lines));
        Snapshot.Match(vt.GetStyledScreen());
    }

    [Fact]
    public void TransientTemplateTokens()
    {
        var theme = LoadTheme("pickle");
        theme.Prompt.TransientTemplate = "[{time}] {cwd} {promptChar} ";
        var preview = new ThemePreview(windowsPaths: false);
        Assert.Equal("[14:05:09] ~/src/pickle/…/Prompt ❯ ", TextWidth.StripAnsi(preview.RenderTransient(theme, 80)));

        var failed = preview.RenderTransient(theme, 80, lastCommandSucceeded: false);
        Assert.Contains(Ansi.Colorize("❯", theme.Prompt.PromptCharErrorColor), failed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SeparatorStyle.Plain)]
    [InlineData(SeparatorStyle.Powerline)]
    [InlineData(SeparatorStyle.Round)]
    [InlineData(SeparatorStyle.Slant)]
    [InlineData(SeparatorStyle.None)]
    public void Separators(SeparatorStyle style)
    {
        RenderedSegment[] segments = [new(" a ", "#000000", "#FF0000"), new(" b ", "#000000", "#00FF00"), new(" c ", "#FFFFFF", null)];
        var vt = new VirtualTerminal(40, 3);
        vt.Write(PromptLayout.Left(segments, style) + "|\n" + PromptLayout.Right(segments, style) + "|");
        Snapshot.Match(vt.GetStyledScreen(), style.ToString());
    }

    [Fact]
    public void NewlineBeforeInputPutsThePromptCharOnItsOwnLine()
    {
        var theme = LoadTheme("pickle");
        theme.Prompt.NewlineBeforeInput = true;
        var render = new ThemePreview(windowsPaths: false).Render(theme, 80, lastCommandSucceeded: true);
        var lines = render.Left.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("❯ ", TextWidth.StripAnsi(lines[1]));
    }

    [Fact]
    public void NarrowTerminalsDropTrailingSegmentsAndTheRightPrompt()
    {
        var theme = LoadTheme("pickle");
        var preview = new ThemePreview(windowsPaths: false);
        var wide = TextWidth.StripAnsi(preview.Render(theme, 120).Left);
        var narrow = preview.Render(theme, 30);
        Assert.Contains("⚙ 1", wide, StringComparison.Ordinal);
        Assert.DoesNotContain("⚙", TextWidth.StripAnsi(narrow.Left), StringComparison.Ordinal);
        Assert.Contains("Prompt", TextWidth.StripAnsi(narrow.Left), StringComparison.Ordinal);
        Assert.Null(narrow.Right);
    }

    [Fact]
    public void EveryBuiltInThemeIsSnapshotted()
    {
        var embedded = typeof(ThemeProvider).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Pickle.Themes.", StringComparison.Ordinal))
            .Select(n => n["Pickle.Themes.".Length..^".json".Length])
            .Order(StringComparer.Ordinal);
        Assert.Equal(BuiltInThemes, embedded);
    }

    internal static Theme LoadTheme(string name)
    {
        using var stream = typeof(ThemeProvider).Assembly.GetManifestResourceStream("Pickle.Themes." + name + ".json")
            ?? throw new InvalidOperationException("No built-in theme " + name);
        return JsonSerializer.Deserialize<Theme>(stream, PickleJson.Options)!;
    }

    /// <summary>The prompt as a line editor draws it: left prompt, right prompt aligned to the last column but one.</summary>
    internal static string Screen(PromptRender render, int width)
    {
        var vt = new VirtualTerminal(width, 6);
        vt.Write(render.Left);
        if (render.Right is { } right)
        {
            var column = vt.CursorColumn;
            vt.Write(Ansi.CursorToColumn(width - TextWidth.VisibleWidth(right)) + right + Ansi.CursorToColumn(column + 1));
        }

        vt.Write("█");
        return vt.GetStyledScreen();
    }
}
