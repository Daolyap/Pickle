using Pickle.Core.Input;
using Pickle.Testing;

namespace Pickle.Core.Tests.Input;

public class TextNavigationTests
{
    [Theory]
    [InlineData("Get-ChildItem -Force", 20, 14)]
    [InlineData("Get-ChildItem -Force", 14, 0)]
    [InlineData("$env:PATH", 9, 5)]
    [InlineData("$env:PATH", 5, 0)]
    [InlineData("cd C:\\Users\\me", 14, 12)]
    [InlineData("a.b(c)", 6, 4)]
    public void WordStartBefore(string text, int from, int expected) =>
        Assert.Equal(expected, TextNavigation.WordStartBefore(text, from));

    [Theory]
    [InlineData("Get-ChildItem -Force", 0, 13)]
    [InlineData("Get-ChildItem -Force", 13, 20)]
    [InlineData("'x' | y", 3, 7)]
    public void WordEndAfter(string text, int from, int expected) =>
        Assert.Equal(expected, TextNavigation.WordEndAfter(text, from));

    [Fact]
    public void GraphemesAndColumns()
    {
        const string text = "a👍b\n日本x";
        Assert.Equal(3, TextNavigation.NextGrapheme(text, 1));
        Assert.Equal(1, TextNavigation.PreviousGrapheme(text, 3));
        Assert.Equal(4, TextNavigation.ColumnOf(text, 7));
        Assert.Equal(6, TextNavigation.IndexAtColumn(text, 5, 3));
        Assert.Equal(7, TextNavigation.IndexAtColumn(text, 5, 4));
        Assert.Equal(2, TextNavigation.LineCount(text));
    }

    [Fact]
    public void ReplRunsMultiLineInput()
    {
        using var t = TestPickle.Create(start: true);
        t.Terminal.Type("if ($true) {").Press("Enter").Type("'yes' }").Press("Enter");
        t.Terminal.OnInputExhausted = () => new ConsoleKeyInfo('\u0004', ConsoleKey.D, false, false, true);
        t.Runtime.Repl.Run();
        var screen = t.Terminal.GetScreenText();
        Assert.Contains("∙ 'yes' }", screen, StringComparison.Ordinal);
        Assert.Contains("\nyes\n", screen, StringComparison.Ordinal);
    }
}
