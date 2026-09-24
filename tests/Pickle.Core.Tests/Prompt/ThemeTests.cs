using System.Reflection;
using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Core.Prompt;
using Pickle.Testing;

namespace Pickle.Core.Tests.Prompt;

public class ThemeTests
{
    public static TheoryData<string> Themes() => new(PromptRenderTests.BuiltInThemes);

    [Theory]
    [MemberData(nameof(Themes))]
    public void BuiltInThemeDefinesEveryField(string name)
    {
        using var stream = typeof(ThemeProvider).Assembly.GetManifestResourceStream("Pickle.Themes." + name + ".json")!;
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        Assert.Equal(name, root.GetProperty("name").GetString());
        AssertAllPropertiesPresent(typeof(Theme), root, name);

        var theme = PromptRenderTests.LoadTheme(name);
        foreach (var (section, value) in Colors(theme))
        {
            Assert.True(
                value == "default" || PickleColor.Parse(value) is not null,
                $"{name}: {section} has an invalid color '{value}'");
        }

        foreach (var (property, value) in Strings(theme.Terminal))
        {
            Assert.True(PickleColor.Parse(value) is { IsRgb: true }, $"{name}: terminal.{property} must be #RRGGBB for Windows Terminal");
        }

        Assert.NotEmpty(theme.Prompt.Left);
        Assert.Contains(theme.Prompt.Left.Concat(theme.Prompt.Right), s => s.Type == "cwd");
    }

    [Fact]
    public void ProviderListsLoadsAndRejectsPathLikeNames()
    {
        using var t = TestPickle.Create();
        var themes = t.Runtime.ThemeProvider;
        Assert.Equal(PromptRenderTests.BuiltInThemes, themes.Available.Order(StringComparer.Ordinal));
        Assert.Equal("powerline", themes.Load("powerline")?.Name);
        Assert.Null(themes.Load("../config"));
        Assert.Null(themes.Load("a/b"));
        Assert.Null(themes.Load(".."));
        Assert.Null(themes.Load("nope"));
        Assert.False(ThemeProvider.IsValidName(@"..\x"));
        Assert.True(ThemeProvider.IsValidName("my-theme_2.dark"));
    }

    [Fact]
    public void UserThemesOverrideBuiltInsAndAreListed()
    {
        using var t = TestPickle.Create();
        File.WriteAllText(Path.Combine(t.Paths.ThemesDir, "mine.json"), """{ "description": "custom", "prompt": { "promptChar": "$" } }""");
        var themes = t.Runtime.ThemeProvider;
        Assert.Contains("mine", themes.Available);
        var mine = themes.Load("mine")!;
        Assert.Equal("mine", mine.Name);
        Assert.Equal("$", mine.Prompt.PromptChar);
        Assert.NotNull(themes.UserThemeFile("mine"));
        Assert.Null(themes.UserThemeFile("pickle"));
    }

    [Fact]
    public void PsStyleFollowsTheTheme()
    {
        using var t = TestPickle.Create(start: true);
        var pickle = t.Runtime.Themes.Current;
        Assert.Equal([Ansi.Style(pickle.Ui.Accent, bold: true)], t.Run("$PSStyle.Formatting.TableHeader"));
        Assert.Equal([Ansi.Style(pickle.Ui.Error, bold: true)], t.Run("$PSStyle.Formatting.Error"));
        Assert.Equal([Ansi.Style(pickle.Syntax.Type, bold: true)], t.Run("$PSStyle.FileInfo.Directory"));

        t.Runtime.ThemeProvider.Apply("mono");
        var mono = t.Runtime.Themes.Current;
        Assert.Equal([Ansi.Style(mono.Ui.Warning, bold: true)], t.Run("$PSStyle.Formatting.Warning"));

        // Switching from inside a pipeline is applied at the next prompt (the runspace is busy until then).
        t.Run("Set-PickleTheme -Name classic");
        var classic = t.Runtime.Themes.Current;
        Assert.Equal("classic", classic.Name);
        t.Runtime.Prompt.Render(t.Runtime.CreatePromptContext());
        Assert.Equal([Ansi.Style(classic.Ui.Accent, bold: true)], t.Run("$PSStyle.Formatting.FormatAccent"));
        Assert.Equal([Ansi.Style(classic.Syntax.Command, bold: true)], t.Run("$PSStyle.FileInfo.Executable"));
    }

    [Fact]
    public void ThemeCmdlets()
    {
        using var t = TestPickle.Create(start: true);
        Assert.Equal(["pickle"], t.Run("(Get-PickleTheme).Name"));
        Assert.Equal(PromptRenderTests.BuiltInThemes, t.Run("Get-PickleTheme -ListAvailable | ForEach-Object Name"));
        Assert.Equal(["True"], t.Run("(Get-PickleTheme -ListAvailable | Where-Object IsCurrent).Name -eq 'pickle'"));
        Assert.Equal(["powerline"], t.Run("(Get-PickleTheme powerline).Name"));
        Assert.Throws<InvalidOperationException>(() => t.Run("Get-PickleTheme -Name nope"));

        Assert.Equal(["minimal"], t.Run("(Set-PickleTheme minimal -PassThru).Name"));
        Assert.Equal("minimal", t.Runtime.Config.Current.Theme);
        Assert.Throws<InvalidOperationException>(() => t.Run("Set-PickleTheme -Name nope"));
        Assert.Equal(["minimal", "mono"], t.Run("(TabExpansion2 -inputScript 'Set-PickleTheme m' -cursorColumn 17).CompletionMatches.CompletionText"));
    }

    [Fact]
    public void PkThemeCommands()
    {
        using var t = TestPickle.Create(width: 100, height: 60, start: true);
        t.Run("pk theme list");
        var screen = t.Terminal.GetScreenText();
        foreach (var name in PromptRenderTests.BuiltInThemes)
        {
            Assert.Contains(name, screen, StringComparison.Ordinal);
        }

        Assert.Contains("● pickle", screen, StringComparison.Ordinal);

        t.Run("pk theme set powerline");
        Assert.Equal("powerline", t.Runtime.Themes.Current.Name);
        Assert.Contains("Theme set to powerline.", t.Terminal.GetScreenText(), StringComparison.Ordinal);

        t.Terminal.Write(Ansi.CursorTo(1, 1) + "\u001b[2J");
        t.Run("pk theme show classic");
        screen = t.Terminal.GetScreenText();
        Assert.Contains("classic", screen, StringComparison.Ordinal);
        Assert.Contains("PS ", screen, StringComparison.Ordinal);
        Assert.Contains("Get-ChildItem -Path 'src'", screen, StringComparison.Ordinal);

        t.Terminal.Write(Ansi.CursorTo(1, 1) + "\u001b[2J");
        t.Run("pk theme preview");
        screen = t.Terminal.GetScreenText();
        Assert.Contains("powerline (current)", screen, StringComparison.Ordinal);
        Assert.Contains("took 3.2s", screen, StringComparison.Ordinal);
        Assert.Contains("[ADMIN] PS ", screen, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => t.Run("pk theme set nope"));
    }

    private static void AssertAllPropertiesPresent(Type type, JsonElement element, string path)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var key = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            Assert.True(element.TryGetProperty(key, out var value), $"{path}.{key} is missing");
            Assert.NotEqual(JsonValueKind.Null, value.ValueKind);
            if (property.PropertyType.IsClass && property.PropertyType != typeof(string) && property.PropertyType.Namespace == typeof(Theme).Namespace)
            {
                AssertAllPropertiesPresent(property.PropertyType, value, path + "." + key);
            }
        }
    }

    private static IEnumerable<(string Name, string? Value)> Colors(Theme theme) =>
        Strings(theme.Terminal).Select(p => ("terminal." + p.Name, p.Value))
            .Concat(Strings(theme.Syntax).Select(p => ("syntax." + p.Name, p.Value)))
            .Concat(Strings(theme.Ui).Select(p => ("ui." + p.Name, p.Value)))
            .Append(("prompt.promptCharColor", theme.Prompt.PromptCharColor))
            .Append(("prompt.promptCharErrorColor", theme.Prompt.PromptCharErrorColor))
            .Concat(theme.Prompt.Left.Concat(theme.Prompt.Right).SelectMany(s => new (string Name, string? Value)[]
            {
                ("prompt." + s.Type + ".foreground", s.Foreground ?? "default"),
                ("prompt." + s.Type + ".background", s.Background ?? "default"),
            }));

    private static IEnumerable<(string Name, string? Value)> Strings(object section) =>
        section.GetType().GetProperties().Where(p => p.PropertyType == typeof(string)).Select(p => (p.Name, (string?)p.GetValue(section)));
}
