using System.Diagnostics;
using System.Text;
using Pickle.Abstractions;
using Pickle.Core.Syntax;
using Pickle.Testing;

namespace Pickle.Core.Tests.Syntax;

public sealed class HighlightFixture : IDisposable
{
    public HighlightFixture()
    {
        Pickle = TestPickle.Create(start: true);
        Highlighter.WhenCommandsLoaded().Wait(TimeSpan.FromMinutes(2));
    }

    public TestPickle Pickle { get; }

    public SyntaxHighlighter Highlighter => (SyntaxHighlighter)Pickle.Runtime.Highlighter;

    public void Dispose() => Pickle.Dispose();
}

public class SyntaxHighlighterTests(HighlightFixture fixture) : IClassFixture<HighlightFixture>
{
    [Theory]
    [InlineData("command", "Get-ChildItem -Path . -Recurse")]
    [InlineData("unknown-command", "definitely-not-a-command --flag value")]
    [InlineData("assignment", "$x = 42 + 3.5")]
    [InlineData("expandable-string", "Write-Output \"Hello $name and $($env:HOME)\"")]
    [InlineData("literal-string", "Write-Output 'single quoted'")]
    [InlineData("keywords", "if ($a -eq 1) { 'one' } else { 'other' }")]
    [InlineData("comment", "Get-Date # trailing comment")]
    [InlineData("type-and-static-member", "[System.IO.Path]::GetTempPath()")]
    [InlineData("members", "$list.Count; $obj.Name.ToUpper()")]
    [InlineData("here-string", "@\"\nhere $x\n\"@")]
    [InlineData("pipeline", "Get-Process | Where-Object { $_.CPU -gt 10 } | Select-Object -First 5")]
    [InlineData("function-defined-inline", "function Invoke-Mine { param($p) }; Invoke-Mine -p 1")]
    [InlineData("parse-error", "Write-Output (1 + ]")]
    [InlineData("incomplete-not-an-error", "foreach ($i in 1..3) {")]
    [InlineData("splat", "$splat = @{ Path = '.' }; Get-Item @splat")]
    [InlineData("missing-script-path", "./not-there.ps1 arg")]
    [InlineData("redirection", "& $cmd -Verbose 2>&1 > out.txt")]
    public void HighlightsRepresentativeInputs(string name, string input)
    {
        Snapshot.Match(Render(input), name);
    }

    [Fact]
    public void UnknownCommandUsesUnknownColor()
    {
        var theme = fixture.Pickle.Runtime.Themes.Current;
        var spans = fixture.Highlighter.Highlight("no-such-cmd-xyz; Get-Item .");
        Assert.Equal(Ansi.Style(theme.Syntax.UnknownCommand), spans[0].Style);
        Assert.Contains(spans, s => s.Start == "no-such-cmd-xyz; ".Length && s.Style == Ansi.Style(theme.Syntax.Command));
    }

    [Fact]
    public void PackageManagersTranslatedToWingetAreNotShownAsUnknown()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "apt/brew/zypper are only translated on Windows");
        var theme = fixture.Pickle.Runtime.Themes.Current;
        Assert.Equal(Ansi.Style(theme.Syntax.Command), fixture.Highlighter.Highlight("zypper install git")[0].Style);
    }

    [Theory]
    [InlineData("export PICKLE_HL_TEST=1")]
    [InlineData("PICKLE_HL_TEST=1 Get-Date")]
    public void CommandsTheTranslatorRewritesAreNotShownAsUnknown(string input)
    {
        var theme = fixture.Pickle.Runtime.Themes.Current;
        Assert.Equal(Ansi.Style(theme.Syntax.Command), fixture.Highlighter.Highlight(input)[0].Style);
    }

    [Fact]
    public void RewritingOnlyTheArgumentsKeepsAnUnknownCommandRed()
    {
        var theme = fixture.Pickle.Runtime.Themes.Current;
        Assert.Equal(Ansi.Style(theme.Syntax.UnknownCommand), fixture.Highlighter.Highlight("no-such-cmd-xyz 2>/dev/null")[0].Style);
    }

    [Fact]
    public void FunctionsDefinedAtRuntimeBecomeKnownAfterCommand()
    {
        var runtime = fixture.Pickle.Runtime;
        var theme = runtime.Themes.Current;
        Assert.Equal(Ansi.Style(theme.Syntax.UnknownCommand), fixture.Highlighter.Highlight("Test-Freshly-Made")[0].Style);

        runtime.Repl.ExecuteLine("function Test-Freshly-Made { }", echo: false);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (fixture.Highlighter.Highlight("Test-Freshly-Made")[0].Style != Ansi.Style(theme.Syntax.Command) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        Assert.Equal(Ansi.Style(theme.Syntax.Command), fixture.Highlighter.Highlight("Test-Freshly-Made")[0].Style);
    }

    [Fact]
    public void TypicalLineIsFast()
    {
        var theme = fixture.Pickle.Runtime.Themes.Current;
        const string line = "Get-ChildItem -Path $HOME -Recurse -Filter *.cs | Where-Object { $_.Length -gt 1kb } | Select-Object -First 10";
        for (var i = 0; i < 20; i++)
        {
            fixture.Highlighter.Compute(line, theme, "/");
        }

        var sw = Stopwatch.StartNew();
        const int runs = 200;
        for (var i = 0; i < runs; i++)
        {
            fixture.Highlighter.Compute(line, theme, "/");
        }

        // Typically well under 1 ms; the bound only catches pathological regressions on a busy machine.
        Assert.True(sw.Elapsed.TotalMilliseconds / runs < 20, $"average {sw.Elapsed.TotalMilliseconds / runs:F3} ms");
    }

    [Fact]
    public void LongInputsAreHighlightedInTheBackground()
    {
        using var t = TestPickle.Create(configure: c => c.Editor.HighlightDebounceThreshold = 300);
        var highlighter = (SyntaxHighlighter)t.Runtime.Highlighter;
        var input = string.Join("; ", Enumerable.Repeat("$value = 'text'", 40));
        var before = highlighter.Version;
        Assert.Empty(highlighter.Highlight(input));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (highlighter.Version == before && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        Assert.NotEmpty(highlighter.Highlight(input));

        // Edits keep the stale colors before the change while the new parse runs.
        var edited = input + " + 1";
        var interim = highlighter.Highlight(edited);
        Assert.NotEmpty(interim);
        Assert.All(interim, s => Assert.True(s.Start + s.Length <= input.Length));
    }

    private string Render(string input)
    {
        var spans = fixture.Highlighter.Highlight(input);
        var sb = new StringBuilder();
        var position = 0;
        foreach (var span in spans)
        {
            sb.Append(input, position, span.Start - position);
            sb.Append(span.Style).Append(input, span.Start, span.Length).Append(Ansi.Reset);
            position = span.Start + span.Length;
        }

        sb.Append(input[position..]);
        var vt = new VirtualTerminal(100, 5);
        vt.Write(sb.ToString());
        return vt.GetStyledScreen();
    }
}
