using Pickle.Abstractions;
using Pickle.Tui.Widgets;

namespace Pickle.Tui.Tests;

public class FuzzyFilterTests
{
    [Fact]
    public void MatchesSubsequencesCaseInsensitively()
    {
        var match = FuzzyFilter.Match("gst", "Git Status");
        Assert.NotNull(match);
        Assert.Equal([0, 4, 5], match.Positions);
        Assert.Null(FuzzyFilter.Match("xyz", "Git Status"));
        Assert.Same(FuzzyMatch.Empty, FuzzyFilter.Match("  ", "anything"));
    }

    [Fact]
    public void AllSpaceSeparatedTermsMustMatch()
    {
        Assert.NotNull(FuzzyFilter.Match("src cs", "src/Program.cs"));
        Assert.Null(FuzzyFilter.Match("src py", "src/Program.cs"));
    }

    [Fact]
    public void RanksWordStartsAndFileNamesHigher()
    {
        string[] items = ["docs/readme-old/notes.txt", "src/ReadMe.md", "tests/rxexaxdxmxe.txt"];
        var ranked = FuzzyFilter.Filter(items, "readme", s => s).Select(r => r.Item).ToList();
        Assert.Equal("src/ReadMe.md", ranked[0]);
        Assert.DoesNotContain("tests/rxexaxdxmxe.txt", ranked.Take(1));
    }

    [Fact]
    public void EmptyPatternKeepsOrderAndLimit()
    {
        var ranked = FuzzyFilter.Filter(["c", "a", "b"], string.Empty, s => s, limit: 2);
        Assert.Equal(["c", "a"], ranked.Select(r => r.Item));
    }

    [Fact]
    public void KeywordsMatchWithoutHighlights()
    {
        var ranked = FuzzyFilter.Filter(["Files", "Jobs"], "panel job", s => s, s => "Panel " + s);
        var (item, match) = Assert.Single(ranked);
        Assert.Equal("Jobs", item);
        Assert.Empty(match.Positions);
    }
}
