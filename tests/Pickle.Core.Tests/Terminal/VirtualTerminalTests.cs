using Pickle.Abstractions;
using Pickle.Testing;

namespace Pickle.Core.Tests.Terminal;

public class VirtualTerminalTests
{
    [Fact]
    public void InterpretsCursorMovementAndErase()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Write("hello world\r" + Ansi.CursorForward(6) + Ansi.ClearToEndOfLine + "there");
        Assert.Equal("hello there", vt.GetScreenText());
    }

    [Fact]
    public void TracksStyles()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Write(Ansi.Colorize("ok", "#00FF00", bold: true) + " plain");
        Assert.Equal("«fg=#00FF00,bold»ok«» plain", vt.GetStyledScreen());
    }

    [Fact]
    public void WrapsAndScrolls()
    {
        var vt = new VirtualTerminal(5, 2);
        vt.Write("abcdefgh\nxyz");
        Assert.Equal("fgh\nxyz", vt.GetScreenText());
        Assert.Equal(["abcde"], vt.Scrollback);
    }

    [Fact]
    public void AlternateScreenRestoresMainScreen()
    {
        var vt = new VirtualTerminal(10, 3);
        vt.Write("main");
        vt.Write("\u001b[?1049h");
        vt.Write("panel");
        Assert.Equal("panel", vt.GetScreenText());
        vt.Write("\u001b[?1049l");
        Assert.Equal("main", vt.GetScreenText());
    }

    [Fact]
    public void ScriptsKeys()
    {
        var vt = new VirtualTerminal();
        vt.Type("ab").Press("Ctrl+R", "Enter");
        Assert.Equal('a', vt.ReadKey().KeyChar);
        Assert.Equal('b', vt.ReadKey().KeyChar);
        var ctrlR = vt.ReadKey();
        Assert.Equal("Ctrl+R", KeyChord.FromKeyInfo(ctrlR).ToString());
        Assert.Equal(ConsoleKey.Enter, vt.ReadKey().Key);
        Assert.Throws<EndOfScriptedInputException>(() => vt.ReadKey());
    }
}
