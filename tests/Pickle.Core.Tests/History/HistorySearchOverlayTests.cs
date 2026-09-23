using Pickle.Abstractions;
using Pickle.Core.History;
using Pickle.Testing;

namespace Pickle.Core.Tests.History;

public class HistorySearchOverlayTests
{
    private const string Session = "session-1";
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly HistoryEntry[] Entries =
    [
        new("git clone https://example.com/repo.git", Now.AddDays(-40), "/home/me/src", true, SessionId: "old"),
        new("git status", Now.AddDays(-2), "/home/me/src/pickle", true, SessionId: "old"),
        new("dotnet build", Now.AddHours(-5), "/home/me/src/pickle", true, SessionId: "old"),
        new("git stash pop", Now.AddHours(-3), "/home/me/src/other", false, SessionId: "old"),
        new("git status", Now.AddMinutes(-30), "/home/me/src/other", true, SessionId: Session),
        new("Get-ChildItem -Recurse -Filter *.cs", Now.AddMinutes(-10), "/home/me/src/pickle", true, SessionId: Session),
        new("git log --oneline", Now.AddSeconds(-20), "/home/me/src/pickle", true, SessionId: Session),
    ];

    [Fact]
    public void ListsDeduplicatedEntriesNewestFirstForAnEmptyQuery()
    {
        var overlay = Create(string.Empty);
        Assert.Equal(
            ["git log --oneline", "Get-ChildItem -Recurse -Filter *.cs", "git status", "git stash pop", "dotnet build", "git clone https://example.com/repo.git"],
            overlay.Results.Select(r => r.Entry.CommandLine));
        Assert.Equal("/home/me/src/other", overlay.Results[2].Entry.Cwd);
    }

    [Fact]
    public void RanksByScoreThenRecency()
    {
        var overlay = Create("gst");
        Assert.Equal(["git status", "git stash pop"], overlay.Results.Select(r => r.Entry.CommandLine).Take(2));
    }

    [Fact]
    public void InitialQueryIsTheBufferText()
    {
        var buffer = new FakeEditorBuffer("dotnet");
        var overlay = Create(buffer.Text);
        Assert.Equal("dotnet", overlay.Query);
        Assert.Equal("dotnet build", overlay.Selected?.CommandLine);
    }

    [Fact]
    public void TypingAndBackspaceEditTheQueryNotTheBuffer()
    {
        var buffer = new FakeEditorBuffer("x");
        buffer.OpenOverlay(Create(buffer.Text, "/home/me/src/pickle"));
        var overlay = (HistorySearchOverlay)buffer.Overlay!;

        buffer.Press("Backspace").Type("stash");
        Assert.Equal("stash", overlay.Query);
        Assert.Equal("x", buffer.Text);
        Assert.Equal("git stash pop", overlay.Selected?.CommandLine);

        buffer.Press("Ctrl+W");
        Assert.Equal(string.Empty, overlay.Query);
        Assert.Contains("history", overlay.InputLineOverride, StringComparison.Ordinal);
    }

    [Fact]
    public void CtrlRCyclesTheScope()
    {
        var buffer = new FakeEditorBuffer();
        var overlay = Create(string.Empty, "/home/me/src/pickle");
        buffer.OpenOverlay(overlay);
        Assert.Equal(HistorySearchScope.All, overlay.Scope);
        Assert.Contains("[all]", TextWidth.StripAnsi(overlay.InputLineOverride!), StringComparison.Ordinal);

        buffer.Press("Ctrl+R");
        Assert.Equal(HistorySearchScope.Directory, overlay.Scope);
        Assert.Equal(["git log --oneline", "Get-ChildItem -Recurse -Filter *.cs", "dotnet build", "git status"], overlay.Results.Select(r => r.Entry.CommandLine));

        buffer.Press("Ctrl+R");
        Assert.Equal(HistorySearchScope.Session, overlay.Scope);
        Assert.Equal(["git log --oneline", "Get-ChildItem -Recurse -Filter *.cs", "git status"], overlay.Results.Select(r => r.Entry.CommandLine));
        Assert.Contains("[session]", TextWidth.StripAnsi(overlay.InputLineOverride!), StringComparison.Ordinal);

        buffer.Press("Ctrl+R");
        Assert.Equal(HistorySearchScope.All, overlay.Scope);
        Assert.Same(overlay, buffer.Overlay);
    }

    [Fact]
    public void EnterReplacesTheBufferWithoutRunningIt()
    {
        var buffer = new FakeEditorBuffer("git");
        buffer.OpenOverlay(Create(buffer.Text));
        buffer.Press("Down", "Enter");

        Assert.Null(buffer.Overlay);
        Assert.Equal("git status", buffer.Text);
        Assert.Equal(buffer.Text.Length, buffer.Cursor);
        Assert.False(buffer.Accepted);
    }

    [Theory]
    [InlineData("Tab")]
    [InlineData("RightArrow")]
    public void TabAndRightAcceptForEditing(string key)
    {
        var buffer = new FakeEditorBuffer("dot");
        buffer.OpenOverlay(Create(buffer.Text));
        buffer.Press(key);
        Assert.Null(buffer.Overlay);
        Assert.Equal("dotnet build", buffer.Text);
        Assert.False(buffer.Accepted);
    }

    [Fact]
    public void EscapeRestoresTheOriginalLine()
    {
        var buffer = new FakeEditorBuffer("git st", cursor: 3);
        buffer.OpenOverlay(Create(buffer.Text, cursor: buffer.Cursor));
        buffer.Type("ash").Press("Escape");
        Assert.Null(buffer.Overlay);
        Assert.Equal("git st", buffer.Text);
        Assert.Equal(3, buffer.Cursor);
    }

    [Fact]
    public void UpAndDownMoveTheSelectionWithinBounds()
    {
        var buffer = new FakeEditorBuffer();
        var overlay = Create(string.Empty);
        buffer.OpenOverlay(overlay);
        buffer.Press("Up");
        Assert.Equal(0, overlay.SelectedIndex);
        buffer.Press("Down", "Down", "PageDown");
        Assert.Equal(overlay.Results.Count - 1, overlay.SelectedIndex);
    }

    [Fact]
    public void RendersResultsWithTimeAndDirectory()
    {
        var overlay = Create("git", "/home/me/src/pickle");
        var vt = new VirtualTerminal(60, 8);
        vt.Write(TextWidth.StripAnsi(overlay.InputLineOverride!) + "\n");
        foreach (var line in overlay.Render(60, 5))
        {
            vt.Write(line + "\n");
        }

        Snapshot.Match(vt.GetScreenText(), "text");
        Snapshot.Match(vt.GetStyledScreen(), "styled");
    }

    [Fact]
    public void RendersNarrowAndEmptyStates()
    {
        var narrow = Create("git", "/home/me/src/pickle").Render(24, 3);
        Assert.All(narrow, line => Assert.True(TextWidth.VisibleWidth(line) <= 24, line));

        var empty = Create("zzzz").Render(40, 5);
        Assert.Equal("  no matching history", TextWidth.StripAnsi(Assert.Single(empty)).TrimEnd());
    }

    [Fact]
    public void RelativeTimes()
    {
        Assert.Equal("now", HistorySearchOverlay.RelativeTime(TimeSpan.FromSeconds(5)));
        Assert.Equal("3m", HistorySearchOverlay.RelativeTime(TimeSpan.FromMinutes(3.5)));
        Assert.Equal("5h", HistorySearchOverlay.RelativeTime(TimeSpan.FromHours(5)));
        Assert.Equal("2d", HistorySearchOverlay.RelativeTime(TimeSpan.FromDays(2)));
        Assert.Equal("2mo", HistorySearchOverlay.RelativeTime(TimeSpan.FromDays(65)));
        Assert.Equal("1y", HistorySearchOverlay.RelativeTime(TimeSpan.FromDays(400)));
    }

    [Fact]
    public void HistorySearchActionOpensTheOverlayWithTheBufferText()
    {
        using var t = TestPickle.Create();
        t.Runtime.InitializeComponents();
        t.Runtime.History.Add(new HistoryEntry("Write-Output hello", DateTimeOffset.Now));

        var action = t.Runtime.KeyBindingRegistry.GetAction(EditorActionNames.HistorySearch);
        Assert.NotNull(action);
        var buffer = new FakeEditorBuffer("hel");
        action.Handler(buffer, CancellationToken.None).AsTask().GetAwaiter().GetResult();

        var overlay = Assert.IsType<HistorySearchOverlay>(buffer.Overlay);
        Assert.Equal("hel", overlay.Query);
        Assert.Equal("Write-Output hello", overlay.Selected?.CommandLine);
    }

    private static HistorySearchOverlay Create(string text, string cwd = "/home/me", int? cursor = null) =>
        new(Entries, Session, cwd, text, cursor ?? text.Length, new UiColors(), maxRows: 10, clock: () => Now);
}
