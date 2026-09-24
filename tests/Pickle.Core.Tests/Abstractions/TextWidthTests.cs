using Pickle.Abstractions;

namespace Pickle.Core.Tests.Abstractions;

public class TextWidthTests
{
    [Theory]
    [InlineData("abc", 3)]
    [InlineData("日本", 4)]
    [InlineData("⚡", 2)]
    [InlineData("✅", 2)]
    [InlineData("🚀", 2)]
    [InlineData("🥒", 2)]
    [InlineData("⚙", 1)]
    [InlineData("⚙️", 2)]
    [InlineData("❯", 1)]
    [InlineData("é", 1)]
    [InlineData("\u001b[31mred\u001b[0m", 3)]
    public void VisibleWidth(string text, int expected) => Assert.Equal(expected, TextWidth.VisibleWidth(text));

    [Fact]
    public void TruncateRespectsWideCharacters() => Assert.Equal("日…", TextWidth.Truncate("日本語", 4));
}
