using Pickle.Abstractions;

namespace Pickle.Core.Tests.Abstractions;

public class KeyChordTests
{
    [Theory]
    [InlineData("ctrl+r", "Ctrl+R")]
    [InlineData("Alt+G", "Alt+G")]
    [InlineData("shift+enter", "Shift+Enter")]
    [InlineData("F1", "F1")]
    [InlineData("Alt+,", "Alt+,")]
    [InlineData("Ctrl+Shift+LeftArrow", "Ctrl+Shift+LeftArrow")]
    [InlineData("ctrl+space", "Ctrl+Spacebar")]
    public void ParsesAndNormalizes(string input, string expected) =>
        Assert.Equal(expected, KeyChord.Parse(input).ToString());

    [Fact]
    public void FromKeyInfoNormalizesControlCharacters()
    {
        var info = new ConsoleKeyInfo('\u0012', 0, false, false, false);
        Assert.Equal("Ctrl+R", KeyChord.FromKeyInfo(info).ToString());
    }

    [Fact]
    public void PunctuationIgnoresShift()
    {
        var info = new ConsoleKeyInfo(',', ConsoleKey.OemComma, shift: false, alt: true, control: false);
        Assert.Equal("Alt+,", KeyChord.FromKeyInfo(info).ToString());
    }
}
