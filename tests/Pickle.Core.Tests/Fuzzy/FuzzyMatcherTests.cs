using System.Diagnostics;
using Pickle.Abstractions;
using Pickle.Testing;

namespace Pickle.Core.Tests.Fuzzy;

public class FuzzyMatcherTests
{
    [Fact]
    public void RequiresSubsequence()
    {
        Assert.NotNull(FuzzyMatcher.Match("gci", "Get-ChildItem"));
        Assert.Null(FuzzyMatcher.Match("gcx", "Get-ChildItem"));
        Assert.Null(FuzzyMatcher.Match("abc", "ab"));
    }

    [Fact]
    public void EmptyQueryMatchesWithZeroScore()
    {
        var match = FuzzyMatcher.Match(string.Empty, "anything");
        Assert.NotNull(match);
        Assert.Equal(0, match.Score);
        Assert.Empty(match.Positions);
    }

    [Fact]
    public void SmartCase()
    {
        Assert.NotNull(FuzzyMatcher.Match("readme", "README.md"));
        Assert.Null(FuzzyMatcher.Match("Readme", "README.md"));
        Assert.NotNull(FuzzyMatcher.Match("README", "README.md"));
    }

    [Fact]
    public void PositionsFollowWordBoundaries()
    {
        var match = FuzzyMatcher.Match("gci", "Get-ChildItem");
        Assert.NotNull(match);
        Assert.Equal([0, 4, 9], match.Positions);

        // 'c' right after "Pickle." beats the earlier mid-word 'c' in "Pickle".
        var path = FuzzyMatcher.Match("pc", "src/Pickle.Core/Completion");
        Assert.NotNull(path);
        Assert.Equal([4, 11], path.Positions);
    }

    [Fact]
    public void PrefersConsecutiveAndBoundaryMatches()
    {
        Score("item", "Get-ChildItem").ShouldBeat(Score("item", "Get-ChildIxxtxexm"));
        Score("fb", "foo_bar").ShouldBeat(Score("fb", "foobar"));
        Score("fb", "fooBar").ShouldBeat(Score("fb", "foobar"));
        Score("git", "git status").ShouldBeat(Score("git", "digital"));
        Score("st", "git status").ShouldBeat(Score("st", "git fast"));
    }

    [Fact]
    public void RankOrdersByScoreThenInputOrder()
    {
        string[] items = ["foobar", "fooBar", "foo_bar", "xyz", "fb", "foo_bar"];
        var ranked = FuzzyMatcher.Rank("fb", items, s => s);
        Assert.Equal(["fb", "foo_bar", "foo_bar", "fooBar", "foobar"], ranked.Select(r => r.Item));
        Assert.All(ranked, r => Assert.Equal(2, r.Match.Positions.Count));
    }

    [Fact]
    public void RankEmptyQueryPreservesOrderAndLimit()
    {
        var ranked = FuzzyMatcher.Rank(string.Empty, Enumerable.Range(0, 10), i => i.ToString(System.Globalization.CultureInfo.InvariantCulture), limit: 3);
        Assert.Equal([0, 1, 2], ranked.Select(r => r.Item));
        Assert.All(ranked, r => Assert.Equal(0, r.Match.Score));
    }

    [Fact]
    public void RankKeepsTheBestWhenLimited()
    {
        var items = Enumerable.Range(0, 500).Select(i => $"q-u-e-r-y-{i}").Append("query").ToList();
        var ranked = FuzzyMatcher.Rank("query", items, s => s, limit: 5);
        Assert.Equal("query", ranked[0].Item);
        Assert.Equal(5, ranked.Count);
    }

    [Fact]
    public void HighlightWrapsMatchedRuns()
    {
        var match = FuzzyMatcher.Match("gci", "Get-ChildItem")!;
        var text = FuzzyMatcher.Highlight("Get-ChildItem", match.Positions, "#E5C07B");
        var vt = new VirtualTerminal(40, 1);
        vt.Write(text);
        Assert.Equal("«fg=#E5C07B,bold»G«»et-«fg=#E5C07B,bold»C«»hild«fg=#E5C07B,bold»I«»tem", vt.GetStyledScreen());
    }

    [Fact]
    public void HighlightRestoresBaseStyle()
    {
        var baseStyle = Ansi.Style("#DDE5D6", "#232922");
        var text = FuzzyMatcher.Highlight("abc", [1, 2], "yellow", baseStyle);
        Assert.Equal(baseStyle + "a" + Ansi.Style("yellow", bold: true) + "bc" + Ansi.Reset + baseStyle, text);
        Assert.Equal(baseStyle + "abc", FuzzyMatcher.Highlight("abc", [], "yellow", baseStyle));
    }

    [Fact]
    public void HandlesLongCandidatesAndQueries()
    {
        var candidate = string.Concat(Enumerable.Repeat("abcdefghij/", 400));
        var match = FuzzyMatcher.Match(new string('a', 300), candidate);
        Assert.NotNull(match);
        Assert.Equal(300, match.Positions.Count);
        Assert.True(match.Positions.SequenceEqual(match.Positions.Order()));
    }

    [Fact]
    public void RanksFiftyThousandItemsQuickly()
    {
        var items = Enumerable.Range(0, 50_000).Select(i => $"git commit -m \"change number {i}\" --author someone{i % 97}").ToList();
        FuzzyMatcher.Rank("gcm", items, s => s);
        var sw = Stopwatch.StartNew();
        var ranked = FuzzyMatcher.Rank("change 4999", items, s => s, limit: 50);
        sw.Stop();
        Assert.NotEmpty(ranked);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"took {sw.ElapsedMilliseconds} ms");
    }

    private static int Score(string query, string candidate) =>
        FuzzyMatcher.Match(query, candidate)?.Score ?? throw new InvalidOperationException($"{query} !~ {candidate}");
}

internal static class ScoreAssertions
{
    public static void ShouldBeat(this int better, int worse) =>
        Assert.True(better > worse, $"expected {better} > {worse}");
}
