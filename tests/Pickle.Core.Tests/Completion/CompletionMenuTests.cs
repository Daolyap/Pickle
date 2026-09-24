using Pickle.Abstractions;
using Pickle.Core.Completion;
using Pickle.Testing;

namespace Pickle.Core.Tests.Completion;

public class CompletionMenuTests
{
    private static readonly CompletionItem[] Parameters =
    [
        new("-Force", "Force", CompletionKind.Parameter, "[switch]"),
        new("-FollowSymlink", "FollowSymlink", CompletionKind.Parameter, "[switch]"),
        new("-Filter", "Filter", CompletionKind.Parameter, "[string]"),
        new("-File", "File", CompletionKind.Parameter, "[switch]"),
    ];

    [Fact]
    public void TabAndArrowsMoveTheSelectionAndWrap()
    {
        var (buffer, menu) = Open("Get-ChildItem -F", Parameters);
        Assert.Equal(0, menu.SelectedIndex);

        buffer.Press("Tab");
        Assert.Equal(1, menu.SelectedIndex);
        buffer.Press("DownArrow", "DownArrow", "Tab");
        Assert.Equal(0, menu.SelectedIndex);
        buffer.Press("Shift+Tab");
        Assert.Equal(3, menu.SelectedIndex);
        buffer.Press("UpArrow");
        Assert.Equal(2, menu.SelectedIndex);
        buffer.Press("PageUp");
        Assert.Equal(0, menu.SelectedIndex);
        buffer.Press("PageDown");
        Assert.Equal(3, menu.SelectedIndex);
        Assert.Equal("Get-ChildItem -F", buffer.Text);
    }

    [Fact]
    public void EnterAcceptsWithoutRunningTheLine()
    {
        var (buffer, menu) = Open("Get-ChildItem -F", Parameters);
        buffer.Press("Tab", "Enter");
        Assert.Null(buffer.Overlay);
        Assert.True(menu.IsClosed);
        Assert.Equal("Get-ChildItem -FollowSymlink", buffer.Text);
        Assert.Equal(buffer.Text.Length, buffer.Cursor);
        Assert.False(buffer.Accepted);
    }

    [Fact]
    public void RightArrowAcceptsAndKeepsTextAfterTheWord()
    {
        var text = "Get-ChildItem -F | Select-Object -First 1";
        var set = new CompletionSet(14, 2, Parameters);
        var buffer = new FakeEditorBuffer(text, cursor: 16);
        buffer.OpenOverlay(new CompletionMenu(set, text, 16, new UiColors()));
        buffer.Press("RightArrow");
        Assert.Equal("Get-ChildItem -Force | Select-Object -First 1", buffer.Text);
        Assert.Equal(20, buffer.Cursor);
    }

    [Fact]
    public void ReplacesTheWholeTokenWhenTheCursorIsInsideIt()
    {
        var text = "Get-Chxx";
        var set = new CompletionSet(0, 8, [new("Get-ChildItem", "Get-ChildItem", CompletionKind.Command), new("Get-Char", "Get-Char", CompletionKind.Command)]);
        var buffer = new FakeEditorBuffer(text, cursor: 6);
        buffer.OpenOverlay(new CompletionMenu(set, text, 6, new UiColors()));
        buffer.Press("Enter");
        Assert.Equal("Get-ChildItem", buffer.Text);
    }

    [Fact]
    public void EscapeClosesAndLeavesTheBufferAlone()
    {
        var (buffer, menu) = Open("Get-ChildItem -F", Parameters);
        buffer.Press("Tab", "Escape");
        Assert.Null(buffer.Overlay);
        Assert.True(menu.IsClosed);
        Assert.Equal("Get-ChildItem -F", buffer.Text);
        Assert.Empty(buffer.Replacements);
    }

    [Fact]
    public void TypingFiltersFuzzilyAndClosesWhenNothingMatches()
    {
        var (buffer, menu) = Open("Get-ChildItem -F", Parameters);
        Assert.Equal(OverlayKeyResult.NotHandled, menu.HandleKey(FakeEditorBuffer.ToKeyInfo("o"), buffer));

        buffer.Type("ol");
        Assert.Equal("Get-ChildItem -Fol", buffer.Text);
        Assert.Equal(["-FollowSymlink"], menu.Items.Select(i => i.CompletionText));

        buffer.Press("Backspace", "Backspace");
        Assert.Equal(4, menu.Items.Count);

        buffer.Type("il");
        Assert.Equal(["-File", "-Filter"], menu.Items.Select(i => i.CompletionText).Order());

        buffer.Type("zz");
        Assert.Null(buffer.Overlay);
        Assert.True(menu.IsClosed);
        Assert.Equal("Get-ChildItem -Filzz", buffer.Text);
    }

    [Fact]
    public void FilteringIsCaseInsensitive()
    {
        var (buffer, menu) = Open("Get-ChildItem -F", Parameters);
        buffer.Type("OLLOW");
        Assert.Equal(["-FollowSymlink"], menu.Items.Select(i => i.CompletionText));
    }

    [Fact]
    public void TypingASpaceOrBackspacingPastTheWordCloses()
    {
        var (buffer, menu) = Open("Get-ChildItem -F", Parameters);
        buffer.Type(" ");
        Assert.Null(buffer.Overlay);

        (buffer, menu) = Open("Get-ChildItem -F", Parameters);
        buffer.Press("Backspace", "Backspace");
        Assert.Equal("Get-ChildItem ", buffer.Text);
        Assert.Null(buffer.Overlay);
        Assert.True(menu.IsClosed);
    }

    [Fact]
    public void SelectLastStartsAtTheEnd()
    {
        var menu = new CompletionMenu(new CompletionSet(14, 2, Parameters), "Get-ChildItem -F", 16, new UiColors(), selectLast: true);
        Assert.Equal(3, menu.SelectedIndex);
    }

    [Fact]
    public void RendersKindsHighlightsDescriptionsAndSelection()
    {
        var (buffer, menu) = Open("Get-ChildItem -Fi", Parameters);
        buffer.Press("Tab");
        Snapshot.Match(Screen(menu.Render(60, 8), 60, 8, styled: false), "text");
        Snapshot.Match(Screen(menu.Render(60, 8), 60, 8, styled: true), "styled");
    }

    [Fact]
    public void ShowsAScrollIndicatorWhenItemsOverflow()
    {
        var items = Enumerable.Range(1, 42).Select(i => new CompletionItem($"item{i:00}.txt", $"item{i:00}.txt", CompletionKind.File)).ToArray();
        var set = new CompletionSet(0, 4, items);
        var buffer = new FakeEditorBuffer("item");
        var menu = new CompletionMenu(set, "item", 4, new UiColors(), maxRows: 5);
        buffer.OpenOverlay(menu);
        buffer.Press("Shift+Tab");

        var lines = menu.Render(40, 10);
        Assert.Equal(5, lines.Count);
        Snapshot.Match(Screen(lines, 40, 6, styled: false));
    }

    [Fact]
    public void TruncatesToTheAvailableWidth()
    {
        CompletionItem[] items =
        [
            new("Get-AVeryLongCommandNameThatKeepsGoingAndGoing", "Get-AVeryLongCommandNameThatKeepsGoingAndGoing", CompletionKind.Command, "A description that is also rather long"),
            new("Get-Short", "Get-Short", CompletionKind.Command, "Short one"),
        ];
        var menu = new CompletionMenu(new CompletionSet(0, 4, items), "Get-", 4, new UiColors());
        foreach (var width in new[] { 80, 40, 20, 10 })
        {
            var lines = menu.Render(width, 10);
            Assert.All(lines, line => Assert.True(TextWidth.VisibleWidth(line) <= width, $"{width}: {TextWidth.StripAnsi(line)}"));
        }

        Snapshot.Match(Screen(menu.Render(40, 10), 40, 3, styled: false));
    }

    [Fact]
    public void HighlightsTheMatchedPartOfFileNames()
    {
        var items = new[] { new CompletionItem("./src/Program.cs", "Program.cs", CompletionKind.File) };
        var menu = new CompletionMenu(new CompletionSet(0, 9, items), "./src/Pro", 9, new UiColors { MatchHighlight = "#FF0000" });
        var line = Screen(menu.Render(40, 3), 40, 1, styled: true);
        Assert.Contains("bold»Pro«", line, StringComparison.Ordinal);
    }

    private static (FakeEditorBuffer Buffer, CompletionMenu Menu) Open(string text, CompletionItem[] items)
    {
        var dash = text.LastIndexOf(' ') + 1;
        var set = new CompletionSet(dash, text.Length - dash, items);
        var buffer = new FakeEditorBuffer(text);
        var menu = new CompletionMenu(set, text, text.Length, new UiColors(), maxRows: 10);
        buffer.OpenOverlay(menu);
        return (buffer, menu);
    }

    private static string Screen(IReadOnlyList<string> lines, int width, int height, bool styled)
    {
        var vt = new VirtualTerminal(width, height);
        vt.Write(string.Join("\n", lines));
        return styled ? vt.GetStyledScreen() : vt.GetScreenText();
    }
}
