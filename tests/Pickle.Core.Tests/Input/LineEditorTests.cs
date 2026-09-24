using Pickle.Abstractions;
using Pickle.Core.Contracts;
using Pickle.Core.Input;
using Pickle.Testing;
using Pickle.Testing.Fakes;

namespace Pickle.Core.Tests.Input;

public class LineEditorTests
{
    [Fact]
    public void TypingAndCursorMovement()
    {
        using var h = EditorHarness.Create();
        h.Type("Get-Item foo").Press("LeftArrow", "LeftArrow", "LeftArrow").Type("x").Press("Home").Type("#").Press("End").Type("!");
        Assert.Equal("#Get-Item xfoo!", h.Pending());
        Snapshot.Match(h.Terminal.GetScreenTextWithCursor());
    }

    [Fact]
    public void AcceptReturnsTextAndMovesBelow()
    {
        using var h = EditorHarness.Create();
        h.Type("Write-Output hi").Press("Enter");
        Assert.Equal("Write-Output hi", h.ReadLine());
        Assert.Equal(1, h.Terminal.CursorRow);
        Assert.Equal(0, h.Terminal.CursorColumn);
        Assert.Equal("> Write-Output hi", h.Terminal.GetScreenText());
    }

    [Fact]
    public void WordMovementUsesPowerShellWords()
    {
        using var h = EditorHarness.Create(60);
        h.Type("Get-ChildItem -Path C:\\Users\\me").Press("Ctrl+LeftArrow");
        h.Pending();
        Assert.Equal("Get-ChildItem -Path C:\\Users\\".Length, h.Editor.Cursor);

        h.Press("Ctrl+LeftArrow", "Ctrl+LeftArrow", "Ctrl+LeftArrow");
        h.Pending();
        Assert.Equal("Get-ChildItem ".Length, h.Editor.Cursor);

        h.Press("Ctrl+W");
        Assert.Equal("-Path C:\\Users\\me", h.Pending());
        h.Press("Ctrl+Delete");
        Assert.Equal(" C:\\Users\\me", h.Pending());
    }

    [Fact]
    public void SoftWrapsAt20Columns()
    {
        using var h = EditorHarness.Create(20);
        h.Type("Write-Output 'abcdefghijklmnopqrstuvwxyz'");
        h.Pending();
        Snapshot.Match(h.Terminal.GetScreenTextWithCursor());
    }

    [Fact]
    public void SoftWrapsAt40ColumnsAndCursorCanMoveBack()
    {
        using var h = EditorHarness.Create(40);
        h.Type("Get-ChildItem -Recurse -Filter *.cs | Select-Object -First 3").Press("Home", "Ctrl+RightArrow");
        h.Pending();
        Snapshot.Match(h.Terminal.GetScreenTextWithCursor());
    }

    [Fact]
    public void ExactWidthInputPutsCursorOnNextRow()
    {
        using var h = EditorHarness.Create(10);
        h.Type("12345678");
        h.Pending();
        Assert.Equal("> 12345678", h.Terminal.GetLine(0));
        Assert.Equal((1, 0), (h.Terminal.CursorRow, h.Terminal.CursorColumn));
    }

    [Fact]
    public void WideCharactersNeverSplitAcrossRows()
    {
        using var h = EditorHarness.Create(10);
        h.Type("'日本語テキスト'").Press("LeftArrow", "LeftArrow");
        h.Pending();
        Snapshot.Match(h.Terminal.GetScreenTextWithCursor());
        h.Press("Backspace");
        Assert.Equal("'日本語テキト'", h.Pending());
    }

    [Fact]
    public void IncompleteInputContinuesOnNextLine()
    {
        using var h = EditorHarness.Create(40);
        h.Type("if ($true) {").Press("Enter").Type("'yes'").Press("Enter").Type("}");
        Assert.Equal("if ($true) {\n'yes'\n}", h.Pending());
        Snapshot.Match(h.Terminal.GetScreenTextWithCursor());
        h.Press("Enter");
        Assert.Equal("if ($true) {\n'yes'\n}", h.ReadLine());
    }

    [Fact]
    public void ShiftEnterInsertsNewlineAndUpDownMoveBetweenLines()
    {
        using var h = EditorHarness.Create(40);
        h.Type("first line").Press("Shift+Enter").Type("2nd").Press("UpArrow");
        h.Pending();
        Assert.Equal("fir".Length, h.Editor.Cursor);
        h.Press("DownArrow", "Home").Type(">");
        Assert.Equal("first line\n>2nd", h.Pending());
        h.Press("Home", "Home");
        h.Pending();
        Assert.Equal(0, h.Editor.Cursor);
        h.Press("End");
        h.Pending();
        Assert.Equal("first line".Length, h.Editor.Cursor);
        h.Press("End");
        h.Pending();
        Assert.Equal(h.Editor.Text.Length, h.Editor.Cursor);
    }

    [Fact]
    public void RightPromptShownUntilInputCollides()
    {
        using var h = EditorHarness.Create(30);
        h.Prompt = new PromptRender("> ", "[12:00]", "∙ ");
        h.Type("ls");
        h.Pending();
        Assert.Equal("> ls".PadRight(23) + "[12:00]", h.Terminal.GetLine(0));

        h.Type(" -Force -Recurse abc");
        h.Pending();
        Assert.Equal("> ls -Force -Recurse abc", h.Terminal.GetLine(0));
    }

    [Fact]
    public void MultiLinePromptKeepsInputOnLastLine()
    {
        using var h = EditorHarness.Create(30);
        h.Prompt = new PromptRender("~/src main\n❯ ", "rp", "  ");
        h.Type("git status");
        h.Pending();
        Snapshot.Match(h.Terminal.GetScreenTextWithCursor());
    }

    [Fact]
    public void AutosuggestionGhostTextAndAccept()
    {
        using var h = EditorHarness.Create(40);
        h.AddHistory("git status", "git commit -m 'wip stuff'");
        h.Type("git c");
        h.Pending();
        Assert.Equal("git commit -m 'wip stuff'", h.Editor.Suggestion);
        Assert.Contains("«fg=#5C6A55»ommit -m 'wip stuff'«»", h.Terminal.GetStyledScreen(), StringComparison.Ordinal);

        h.Press("Ctrl+RightArrow");
        Assert.Equal("git commit", h.Pending());
        h.Press("RightArrow");
        Assert.Equal("git commit -m 'wip stuff'", h.Pending());
        Assert.Null(h.Editor.Suggestion);
        h.Press("Enter");
        Assert.Equal("git commit -m 'wip stuff'", h.ReadLine());
    }

    [Fact]
    public void SuggestionHiddenWhenCursorNotAtEndAndNotKeptAfterAccept()
    {
        using var h = EditorHarness.Create(40);
        h.AddHistory("Get-Process pwsh");
        h.Type("Get-P").Press("LeftArrow");
        h.Pending();
        Assert.Null(h.Editor.Suggestion);
        Assert.Equal("> Get-P", h.Terminal.GetLine(0));

        h.Press("End");
        h.Pending();
        Assert.Equal("> Get-Process pwsh", h.Terminal.GetLine(0));
        h.Press("Enter");
        Assert.Equal("Get-P", h.ReadLine());
        Assert.Equal("> Get-P", h.Terminal.GetLine(0));
    }

    [Fact]
    public void UndoRedoCoalescesTyping()
    {
        using var h = EditorHarness.Create();
        h.Type("echo hello world").Press("Ctrl+Z");
        Assert.Equal("echo hello", h.Pending());
        h.Press("Ctrl+Z");
        Assert.Equal("echo", h.Pending());
        h.Press("Ctrl+Y");
        Assert.Equal("echo hello", h.Pending());
        h.Press("Backspace", "Backspace", "Ctrl+Z");
        Assert.Equal("echo hello", h.Pending());
        h.Press("Ctrl+Z", "Ctrl+Z", "Ctrl+Z");
        Assert.Equal(string.Empty, h.Pending());
    }

    [Fact]
    public void SelectionCutAndPaste()
    {
        using var h = EditorHarness.Create();
        var clipboard = new TerminalClipboard(h.Terminal, emitOsc52: false);
        h.Editor.ClipboardService = clipboard;
        h.Type("Get-Item alpha beta").Press("Ctrl+Shift+LeftArrow", "Shift+LeftArrow");
        h.Pending();
        Assert.Contains("«bg=#3A4A32»", h.Terminal.GetStyledScreen(), StringComparison.Ordinal);

        h.Press("Ctrl+X");
        Assert.Equal("Get-Item alpha", h.Pending());
        Assert.Equal(" beta", clipboard.GetText());

        h.Press("Home", "Ctrl+V");
        Assert.Equal(" betaGet-Item alpha", h.Pending());

        h.Press("Ctrl+A").Type("x");
        Assert.Equal("x", h.Pending());
    }

    [Fact]
    public void CtrlCCopiesSelectionInsteadOfCancelling()
    {
        using var h = EditorHarness.Create();
        var clipboard = new TerminalClipboard(h.Terminal, emitOsc52: false);
        h.Editor.ClipboardService = clipboard;
        h.Type("hello world").Press("Shift+Home", "Ctrl+C");
        Assert.Equal("hello world", h.Pending());
        Assert.Equal("hello world", clipboard.GetText());
        Assert.Null(h.Editor.Selection);
    }

    [Fact]
    public void OscClipboardEmitsBase64()
    {
        var vt = new VirtualTerminal();
        new TerminalClipboard(vt).SetText("hi");
        Assert.Equal("\u001b]52;c;aGk=\u0007", vt.RawOutput);
    }

    [Fact]
    public void HistoryNavigationMatchesTypedPrefix()
    {
        using var h = EditorHarness.Create();
        h.AddHistory("git status", "ls -la", "git log", "git status", "echo 1");
        h.Press("UpArrow");
        Assert.Equal("echo 1", h.Pending());
        h.Press("Escape").Type("git").Press("UpArrow");
        Assert.Equal("git status", h.Pending());
        h.Press("UpArrow");
        Assert.Equal("git log", h.Pending());
        h.Press("UpArrow", "UpArrow");
        Assert.Equal("git log", h.Pending());
        h.Press("DownArrow");
        Assert.Equal("git status", h.Pending());
        h.Press("DownArrow");
        Assert.Equal("git", h.Pending());
    }

    [Fact]
    public void HistoryPreviousContinuesThroughMultiLineEntries()
    {
        using var h = EditorHarness.Create();
        h.AddHistory("first", "if (1) {\n2\n}", "last");
        h.Press("UpArrow", "UpArrow");
        Assert.Equal("if (1) {\n2\n}", h.Pending());
        h.Press("UpArrow");
        Assert.Equal("first", h.Pending());
    }

    [Fact]
    public void OverlayGetsKeysFirstAndRendersBelow()
    {
        using var h = EditorHarness.Create(30);
        var overlay = new TestOverlay();
        h.Runtime.KeyBindingRegistry.RegisterAction("test.overlay", "test", (buffer, _) =>
        {
            buffer.OpenOverlay(overlay);
            return ValueTask.CompletedTask;
        });
        h.Runtime.KeyBindingRegistry.Bind("F5", "test.overlay");

        h.Type("ab").Press("F5").Type("c");
        Assert.Equal("abc", h.Pending());
        Assert.Equal(["abc"], overlay.Changes);
        Snapshot.Match(h.Terminal.GetScreenText());

        h.Press("DownArrow");
        h.Pending();
        Assert.Equal(1, overlay.Selected);

        h.Press("Escape");
        Assert.Equal("abc", h.Pending());
        Assert.Null(h.Editor.Overlay);
        Assert.Equal("> abc", h.Terminal.GetScreenText());
    }

    [Fact]
    public void OverlayInputLineOverrideReplacesInput()
    {
        using var h = EditorHarness.Create(30);
        var overlay = new TestOverlay { Override = "search: gi" };
        h.Type("x");
        h.Pending();
        h.Editor.OpenOverlay(overlay);
        h.Type("y");
        h.Pending();
        Assert.Equal("> search: gi\n> item one\n  item two", h.Terminal.GetScreenText());
    }

    [Fact]
    public void AcceptClosesOverlayAndShowsTransientPrompt()
    {
        using var h = EditorHarness.Create(40, configure: c => c.Prompt.TransientPrompt = true);
        h.Prompt = new PromptRender("~/very/long/path\n$ ", "right", "  ");
        var overlay = new TestOverlay();
        h.AddHistory("Get-Date -Format yyyy");
        h.Type("Get-Date");
        h.Pending();
        h.Editor.OpenOverlay(overlay);
        h.Press("Enter");
        Assert.Equal("Get-Date", h.ReadLine());
        Assert.Equal("❯ Get-Date", h.Terminal.GetScreenText());
        Assert.Equal((1, 0), (h.Terminal.CursorRow, h.Terminal.CursorColumn));
    }

    [Fact]
    public void CtrlCShowsMarkerAndReturnsEmpty()
    {
        using var h = EditorHarness.Create();
        h.Type("Remove-Item *").Press("Ctrl+C");
        Assert.Equal(string.Empty, h.ReadLine());
        Assert.Equal("> Remove-Item *^C", h.Terminal.GetScreenText());
        Assert.Equal(1, h.Terminal.CursorRow);
    }

    [Fact]
    public void CtrlDOnEmptyReturnsNullOtherwiseDeletes()
    {
        using var h = EditorHarness.Create();
        h.Type("ab").Press("Home", "Ctrl+D");
        Assert.Equal("b", h.Pending());

        h.Press("Ctrl+K", "Ctrl+D");
        Assert.Null(h.ReadLine());
    }

    [Fact]
    public void PasteBurstIsInsertedLiterallyAndNeverRuns()
    {
        using var h = EditorHarness.Create(40);
        h.Terminal.Paste("Write-Output 1\nWrite-Output 2\n");
        Assert.Equal("Write-Output 1\nWrite-Output 2", h.Pending());
        Snapshot.Match(h.Terminal.GetScreenText());

        h.Press("Ctrl+Z");
        Assert.Equal(string.Empty, h.Pending());
        h.Press("Ctrl+Y", "Enter");
        Assert.Equal("Write-Output 1\nWrite-Output 2", h.ReadLine());
    }

    [Fact]
    public void ResizeRedrawsAtNewWidth()
    {
        using var h = EditorHarness.Create(40);
        h.Type("Get-ChildItem -Recurse -Force");
        h.Pending();
        h.Terminal.Resize(20, 10);
        h.Type(" x");
        h.Pending();
        Snapshot.Match(h.Terminal.GetScreenTextWithCursor());
    }

    [Fact]
    public void EditorRequestsAreApplied()
    {
        using var h = EditorHarness.Create();
        h.Runtime.Engine.InsertText("Get-Date");
        h.Type(" -Format o");
        Assert.Equal("Get-Date -Format o", h.Pending());

        h.Runtime.Engine.ReplaceInput("Get-Location");
        h.Type("!");
        Assert.Equal("Get-Location!", h.Pending());
    }

    [Fact]
    public void ShowPanelAppliesResultAndRedraws()
    {
        using var h = EditorHarness.Create();
        var host = new FakePanelHost { NextResult = new PanelResult(PanelResultKind.InsertText, "C:\\temp") };
        h.Runtime.ServiceRegistry.Add<IPanelHost>(host);
        h.Runtime.KeyBindingRegistry.RegisterAction("panel.test", "test", (buffer, _) =>
        {
            buffer.ShowPanel("test", "arg");
            return ValueTask.CompletedTask;
        });
        h.Runtime.KeyBindingRegistry.Bind("F6", "panel.test");

        h.Type("cd ").Press("F6");
        Assert.Equal("cd C:\\temp", h.Pending());
        Assert.Equal([("test", "arg", "cd ")], host.Shown);
        Assert.Equal("> cd C:\\temp", h.Terminal.GetScreenText());

        host.NextResult = new PanelResult(PanelResultKind.ChangeDirectory, "/tmp/it's");
        h.Press("F6");
        Assert.Equal("Set-Location -LiteralPath '/tmp/it''s'", h.ReadLine());
    }

    [Fact]
    public void ShowPanelWithoutHostIsNoOp()
    {
        using var h = EditorHarness.Create();
        h.Type("x");
        h.Pending();
        h.Editor.ShowPanel("git");
        Assert.Equal("x", h.Editor.Text);
    }

    [Fact]
    public void EscapeClearsLineAndUndoRestoresIt()
    {
        using var h = EditorHarness.Create();
        h.Type("some text").Press("Escape");
        Assert.Equal(string.Empty, h.Pending());
        h.Press("Ctrl+Z");
        Assert.Equal("some text", h.Pending());
    }

    [Fact]
    public void ClearScreenRedrawsAtTop()
    {
        using var h = EditorHarness.Create();
        h.Terminal.Write("old output\nmore\n");
        h.Type("abc").Press("Ctrl+L");
        h.Pending();
        Assert.Equal("> abc", h.Terminal.GetScreenText());
    }

    [Fact]
    public void PartialOutputLineIsPreserved()
    {
        using var h = EditorHarness.Create(20);
        h.Terminal.Write("no newline");
        h.Type("x");
        h.Pending();
        Assert.Equal("no newline%\n> x", h.Terminal.GetScreenText());
    }

    [Fact]
    public void BellRespectsBellStyle()
    {
        using var h = EditorHarness.Create(configure: c => c.Editor.BellStyle = "audible");
        h.Press("Backspace");
        h.Pending();
        Assert.Contains("\u0007", h.Terminal.RawOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadSimpleLineMasksInput()
    {
        using var h = EditorHarness.Create();
        h.Terminal.Write("Password: ");
        h.Type("s3cret").Press("Backspace", "Enter");
        Assert.Equal("s3cre", h.Editor.ReadSimpleLine(string.Empty, mask: true));
        Assert.Equal("Password: •••••", h.Terminal.GetScreenText());
        Assert.False(h.Terminal.EditMode);
    }

    [Fact]
    public void ReadSimpleLineSupportsEditingAndCancel()
    {
        using var h = EditorHarness.Create();
        h.Type("wrld").Press("LeftArrow", "LeftArrow", "LeftArrow").Type("o").Press("Home").Type("hi ").Press("Enter");
        Assert.Equal("hi world", h.Editor.ReadSimpleLine("Name: ", mask: false));
        h.Type("abc").Press("Ctrl+C");
        Assert.Null(h.Editor.ReadSimpleLine(string.Empty, mask: false));
    }

    [Fact]
    public void RendersOnlyChangedCells()
    {
        using var h = EditorHarness.Create(80);
        h.Type("Write-Output 12345");
        h.Pending();
        h.Terminal.ClearRawOutput();
        h.Type("6");
        h.Pending();
        var raw = h.Terminal.RawOutput;
        Assert.DoesNotContain("Write-Output", raw, StringComparison.Ordinal);
        Assert.Contains("6", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void AllEditingActionsAreRegisteredAndBound()
    {
        using var h = EditorHarness.Create();
        string[] editing =
        [
            EditorActionNames.AcceptLine, EditorActionNames.InsertNewline, EditorActionNames.CancelLine, EditorActionNames.ClearLine,
            EditorActionNames.BackwardChar, EditorActionNames.ForwardChar, EditorActionNames.BackwardWord, EditorActionNames.ForwardWord,
            EditorActionNames.BeginningOfLine, EditorActionNames.EndOfLine, EditorActionNames.BackwardDeleteChar, EditorActionNames.DeleteChar,
            EditorActionNames.BackwardKillWord, EditorActionNames.KillWord, EditorActionNames.KillToEnd, EditorActionNames.Undo,
            EditorActionNames.Redo, EditorActionNames.SelectBackwardChar, EditorActionNames.SelectForwardChar,
            EditorActionNames.SelectBackwardWord, EditorActionNames.SelectForwardWord, EditorActionNames.SelectToStart,
            EditorActionNames.SelectToEnd, EditorActionNames.SelectAll, EditorActionNames.Copy, EditorActionNames.Cut,
            EditorActionNames.Paste, EditorActionNames.HistoryPrevious, EditorActionNames.HistoryNext, EditorActionNames.AcceptSuggestion,
            EditorActionNames.AcceptSuggestionWord, EditorActionNames.ClearScreen, EditorActionNames.ExitIfEmpty,
        ];
        var bound = DefaultKeyBindings.Table.Values.ToHashSet();
        Assert.All(editing, name =>
        {
            Assert.NotNull(h.Runtime.KeyBindingRegistry.GetAction(name));
            Assert.Contains(name, bound);
        });
    }

    private sealed class TestOverlay : IEditorOverlay
    {
        public List<string> Changes { get; } = [];

        public int Selected { get; private set; }

        public string? Override { get; set; }

        public string? InputLineOverride => Override;

        public OverlayKeyResult HandleKey(ConsoleKeyInfo key, IEditorBuffer buffer) => key.Key switch
        {
            ConsoleKey.DownArrow => Select(1),
            ConsoleKey.UpArrow => Select(0),
            ConsoleKey.Escape => OverlayKeyResult.Close,
            _ => OverlayKeyResult.NotHandled,
        };

        public IReadOnlyList<string> Render(int width, int maxRows) =>
            [(Selected == 0 ? "> " : "  ") + "item one", (Selected == 1 ? "> " : "  ") + "item two"];

        public void OnBufferChanged(IEditorBuffer buffer) => Changes.Add(buffer.Text);

        private OverlayKeyResult Select(int index)
        {
            Selected = index;
            return OverlayKeyResult.Handled;
        }
    }
}
