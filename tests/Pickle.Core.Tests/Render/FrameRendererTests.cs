using Pickle.Abstractions;
using Pickle.Core.Render;
using Pickle.Testing;

namespace Pickle.Core.Tests.Render;

public class FrameRendererTests
{
    [Fact]
    public void BuilderWrapsAndTracksCursor()
    {
        var builder = new FrameBuilder(5);
        builder.Write("abc", string.Empty);
        builder.MarkCursor();
        builder.Write("defgh", string.Empty);
        var frame = builder.Build();
        Assert.Equal(["abcde", "fgh"], frame.RowTexts);
        Assert.Equal((0, 3), (frame.CursorRow, frame.CursorColumn));
    }

    [Fact]
    public void BuilderExpandsTabsAndShowsControlCharacters()
    {
        var builder = new FrameBuilder(20);
        builder.Write("a\tb\u0007", string.Empty);
        Assert.Equal(["a   b^G"], builder.Build().RowTexts);
    }

    [Fact]
    public void BuilderParsesSgrAndClipsMenus()
    {
        var builder = new FrameBuilder(6);
        builder.WriteAnsi(Ansi.Colorize("red", "red") + "plain-and-long", clip: true);
        var frame = builder.Build();
        Assert.Equal(["redpla"], frame.RowTexts);
        Assert.Equal("\u001b[31m", frame.Rows[0][0].Style);
        Assert.Equal(string.Empty, frame.Rows[0][3].Style);
    }

    [Fact]
    public void BuilderMeasuresAnsiText()
    {
        Assert.Equal(4, FrameBuilder.MeasureAnsi(Ansi.Colorize("日本", "green")));
    }

    [Fact]
    public void GrowingFrameScrollsAtBottomAndShrinkingClears()
    {
        var vt = new VirtualTerminal(10, 3);
        vt.Write("line1\nline2\n");
        var renderer = new FrameRenderer(vt);
        renderer.Reset();
        renderer.Render(Frame(10, "a", "b", "c"));
        Assert.Equal("line2\na\nb\nc".Split('\n')[1..], vt.GetScreenText().Split('\n'));

        renderer.Render(Frame(10, "a", "B"));
        Assert.Equal("a\nB", vt.GetScreenText());
        Assert.Equal((1, 1), (vt.CursorRow, vt.CursorColumn));

        renderer.Finish();
        vt.Write("next");
        Assert.Equal("a\nB\nnext", vt.GetScreenText());
    }

    [Fact]
    public void UnchangedFrameWritesNothing()
    {
        var vt = new VirtualTerminal(20, 5);
        var renderer = new FrameRenderer(vt);
        renderer.Reset();
        renderer.Render(Frame(20, "hello"));
        vt.ClearRawOutput();
        renderer.Render(Frame(20, "hello"));
        Assert.Equal(string.Empty, vt.RawOutput);

        renderer.Render(Frame(20, "help"));
        Assert.StartsWith(Ansi.BeginSynchronizedUpdate + Ansi.HideCursor, vt.RawOutput, StringComparison.Ordinal);
        Assert.EndsWith(Ansi.ShowCursor + Ansi.EndSynchronizedUpdate, vt.RawOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("hel", vt.RawOutput, StringComparison.Ordinal);
        Assert.Equal("help", vt.GetScreenText());
    }

    [Fact]
    public void IndentedFrameLeavesTextBeforeItAlone()
    {
        var vt = new VirtualTerminal(10, 5);
        vt.Write("Name: ");
        var renderer = new FrameRenderer(vt);
        renderer.Reset(indent: 6);
        var builder = new FrameBuilder(10, indent: 6);
        builder.Write("abcdef", string.Empty);
        renderer.Render(builder.Build());
        Assert.Equal("Name: abcd\nef", vt.GetScreenText());
    }

    private static Frame Frame(int width, params string[] rows)
    {
        var builder = new FrameBuilder(width);
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                builder.NewLine();
            }

            builder.Write(rows[i], string.Empty);
        }

        return builder.Build();
    }
}
