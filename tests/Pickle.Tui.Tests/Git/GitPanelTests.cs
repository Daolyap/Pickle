using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.Git;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using Terminal.Gui.Views;

namespace Pickle.Tui.Tests.Git;

public class GitPanelTests
{
    private const string Sha1 = "1111111111111111111111111111111111111111";
    private const string Sha2 = "2222222222222222222222222222222222222222";

    private const string TwoHunkDiff =
        "diff --git a/m.txt b/m.txt\n" +
        "index 1111111..2222222 100644\n" +
        "--- a/m.txt\n" +
        "+++ b/m.txt\n" +
        "@@ -1,4 +1,4 @@\n" +
        " one\n" +
        "-two\n" +
        "+TWO\n" +
        " three\n" +
        " four\n" +
        "@@ -10,3 +10,5 @@ section\n" +
        " ten\n" +
        "+new a\n" +
        "+new b\n" +
        " eleven\n" +
        " twelve\n";

    private static GitStatus SampleStatus(string? upstream = "origin/main", string? operation = null) => new(
        Root: "/repo",
        Branch: "main",
        Upstream: upstream,
        Ahead: 2,
        Behind: 1,
        IsDetached: false,
        HeadSha: Sha1,
        Entries:
        [
            new GitStatusEntry("c.txt", null, GitChangeKind.None, GitChangeKind.Unmerged),
            new GitStatusEntry("a.txt", null, GitChangeKind.Added, GitChangeKind.None),
            new GitStatusEntry("m.txt", null, GitChangeKind.None, GitChangeKind.Modified),
            new GitStatusEntry("u.txt", null, GitChangeKind.Untracked, GitChangeKind.Untracked),
        ],
        StashCount: 1,
        Operation: operation);

    [Fact]
    public void RendersHeaderSectionsAndColoredDiff()
    {
        var git = new FakeGitService { Status = SampleStatus(operation: "merge"), Diff = TwoHunkDiff };
        using var h = Harness.Open(git);

        var screen = h.Screen();
        Assert.Contains("⎇ main → origin/main ↑2 ↓1", screen, StringComparison.Ordinal);
        Assert.Contains("MERGE in progress", screen, StringComparison.Ordinal);
        Assert.Contains("1 stash", screen, StringComparison.Ordinal);
        Assert.Contains("Conflicts (1)", screen, StringComparison.Ordinal);
        Assert.Contains("Staged (1)", screen, StringComparison.Ordinal);
        Assert.Contains("Changes (1)", screen, StringComparison.Ordinal);
        Assert.Contains("Untracked (1)", screen, StringComparison.Ordinal);
        Assert.Contains("U c.txt", screen, StringComparison.Ordinal);
        Assert.Contains("A a.txt", screen, StringComparison.Ordinal);
        Assert.Contains("@@ -1,4 +1,4 @@", screen, StringComparison.Ordinal);
        Assert.Contains("+TWO", screen, StringComparison.Ordinal);

        var ui = h.Pickle.Runtime.Themes.Current.Ui;
        Assert.Equal(GitLinesView.ToColor(ui.Success), h.ForegroundAt("+TWO"));
        Assert.Equal(GitLinesView.ToColor(ui.Error), h.ForegroundAt("-two"));
        Assert.Equal(GitLinesView.ToColor(ui.Info), h.ForegroundAt("@@ -10,3"));
    }

    [Fact]
    public void FileKeysCallTheService()
    {
        var git = new FakeGitService { Status = SampleStatus(), Diff = TwoHunkDiff };
        using var h = Harness.Open(git);

        h.Press(Key.Space);
        Assert.Equal("stage c.txt", git.Calls[^1]);

        h.Press(Key.CursorDown, Key.Space);
        Assert.Equal("unstage a.txt", git.Calls[^1]);

        h.Press(Key.S.WithShift);
        Assert.Equal("stage .", git.Calls[^1]);
        h.Press(Key.U.WithShift);
        Assert.Equal("unstage .", git.Calls[^1]);

        h.Press(Key.F);
        Assert.Equal("fetch", git.Calls[^1]);
        h.Press(Key.P);
        Assert.Equal("pull", git.Calls[^1]);
        h.Press(Key.P.WithShift);
        Assert.Equal("push", git.Calls[^1]);
        Assert.Equal(["Fetch", "Pull", "Push"], h.Prompts.Infos.Select(i => i.Title));
        h.Press(Key.F5);
        Assert.Empty(h.Prompts.Errors);
    }

    [Fact]
    public void PushWithoutUpstreamAsksFirst()
    {
        var git = new FakeGitService { Status = SampleStatus(upstream: null) };
        using var h = Harness.Open(git);

        h.Prompts.ConfirmAnswer = false;
        h.Press(Key.P.WithShift);
        Assert.DoesNotContain("push", git.Calls);
        Assert.Contains("no upstream", h.Prompts.Confirms[^1], StringComparison.Ordinal);

        h.Prompts.ConfirmAnswer = true;
        h.Press(Key.P.WithShift);
        Assert.Equal("push", git.Calls[^1]);
    }

    [Fact]
    public void DiscardNeedsConfirmation()
    {
        var git = new FakeGitService { Status = SampleStatus(), Diff = TwoHunkDiff };
        using var h = Harness.Open(git);
        h.Press(Key.CursorDown, Key.CursorDown);

        h.Prompts.ConfirmAnswer = false;
        h.Press(Key.D);
        Assert.DoesNotContain(git.Calls, c => c.StartsWith("discard", StringComparison.Ordinal));

        h.Prompts.ConfirmAnswer = true;
        h.Press(Key.D);
        Assert.Equal("discard m.txt", git.Calls[^1]);

        h.Press(Key.CursorUp);
        h.Press(Key.D);
        Assert.Equal(["unstage a.txt", "discard a.txt"], git.Calls.TakeLast(2));

        h.Press(Key.Home, Key.D);
        Assert.Contains("Resolve the conflict", h.Prompts.Errors[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void HunkAndLineStaging()
    {
        var git = new FakeGitService { Status = SampleStatus(), Diff = TwoHunkDiff };
        using var h = Harness.Open(git);
        var file = DiffParser.Parse(TwoHunkDiff)[0];

        h.Press(Key.CursorDown, Key.CursorDown);
        h.Press(Key.U);
        Assert.Contains("under Staged", h.Prompts.Errors[^1], StringComparison.Ordinal);

        h.Press(Key.S);
        Assert.Equal("apply " + DiffParser.BuildHunkPatch(file, file.Hunks[0]).Length, git.Calls[^1]);

        h.Press(new Key(']'));
        h.Press(Key.S);
        Assert.Equal("apply " + DiffParser.BuildHunkPatch(file, file.Hunks[1]).Length, git.Calls[^1]);

        // Focus the diff, move from the hunk header to "+new a" (hunk 2, line 1), mark it and stage only that line.
        h.Press(new Key(']'));
        h.Press(Key.Enter);
        h.Press(Key.CursorDown, Key.CursorDown);
        h.Press(Key.V);
        Assert.Contains("●+new a", h.Screen(), StringComparison.Ordinal);
        h.Press(Key.S);
        var expected = DiffParser.BuildLinePatch(file, file.Hunks[1], new HashSet<int> { 1 }, reverse: false)!;
        Assert.Equal("apply " + expected.Length, git.Calls[^1]);

        h.Press(new Key('['));
        Assert.Empty(h.Prompts.Errors.Skip(1));
    }

    [Fact]
    public void UnstageHunkFromStagedFile()
    {
        var git = new FakeGitService { Status = SampleStatus(), Diff = TwoHunkDiff };
        using var h = Harness.Open(git);
        var file = DiffParser.Parse(TwoHunkDiff)[0];

        h.Press(Key.CursorDown);
        h.Press(Key.S);
        Assert.Contains("under Changes", h.Prompts.Errors[^1], StringComparison.Ordinal);
        h.Press(Key.U);
        Assert.Equal("unapply " + DiffParser.BuildHunkPatch(file, file.Hunks[0]).Length, git.Calls[^1]);
    }

    [Fact]
    public void BranchesView()
    {
        var git = new FakeGitService { Status = SampleStatus() };
        git.Branches.AddRange(
        [
            new GitBranch("main", true, false, "origin/main", "init", DateTimeOffset.Now),
            new GitBranch("feature", false, false, null, "work", DateTimeOffset.Now),
            new GitBranch("origin/topic", false, true, null, "remote work", DateTimeOffset.Now),
        ]);
        using var h = Harness.Open(git);

        h.Press(Key.B);
        var screen = h.Screen();
        Assert.Contains("Local (2)", screen, StringComparison.Ordinal);
        Assert.Contains("* main  → origin/main", screen, StringComparison.Ordinal);
        Assert.Contains("Remote (1)", screen, StringComparison.Ordinal);
        Assert.Contains("Enter checkout", screen, StringComparison.Ordinal);

        h.Press(Key.CursorDown, Key.Enter);
        Assert.Equal("checkout feature", git.Calls[^1]);

        h.Press(Key.CursorDown, Key.Enter);
        Assert.Equal("checkout topic", git.Calls[^1]);
        h.Press(Key.D);
        Assert.Contains("Remote branches", h.Prompts.Errors[^1], StringComparison.Ordinal);

        h.Press(Key.CursorUp);
        h.Prompts.ConfirmAnswer = true;
        h.Press(Key.D);
        Assert.Equal("branch -d feature", git.Calls[^1]);

        h.Prompts.TextAnswer = "new-branch";
        h.Press(Key.N);
        Assert.Equal("checkout -b new-branch", git.Calls[^1]);

        h.Press(Key.Esc);
        Assert.Contains("Conflicts (1)", h.Screen(), StringComparison.Ordinal);
    }

    [Fact]
    public void LogViewShowsCommitDetails()
    {
        var git = new FakeGitService { Status = SampleStatus() };
        git.Log.AddRange(
        [
            new GitCommit(Sha2, "2222222", "Ann", DateTimeOffset.Now, "Second change", ["HEAD -> main"], "*"),
            new GitCommit(Sha1, "1111111", "Bob", DateTimeOffset.Now, "First change", [], "*"),
        ]);
        using var h = Harness.Open(git);

        h.Press(Key.L);
        var screen = h.Screen();
        Assert.Contains("* 2222222 (HEAD -> main) Sec", screen, StringComparison.Ordinal);
        Assert.Contains("* 1111111 First change", screen, StringComparison.Ordinal);
        Assert.Equal("show --stat --format=fuller " + Sha2, git.Calls[^1]);

        h.Press(Key.CursorDown);
        Assert.Equal("show --stat --format=fuller " + Sha1, git.Calls[^1]);

        h.Press(Key.L);
        Assert.Contains("Staged (1)", h.Screen(), StringComparison.Ordinal);
    }

    [Fact]
    public void StashView()
    {
        var git = new FakeGitService { Status = SampleStatus() };
        git.Stashes.AddRange([new GitStash(0, "stash@{0}", "On main: wip"), new GitStash(1, "stash@{1}", "older")]);
        using var h = Harness.Open(git);

        h.Press(Key.Z);
        Assert.Contains("stash@{0}  On main: wip", h.Screen(), StringComparison.Ordinal);
        Assert.Equal("stash show -p --include-untracked --no-color stash@{0}", git.Calls[^1]);

        // Every action refreshes, which re-reads the selected stash's patch, so look for the action call itself.
        h.Press(Key.Enter);
        Assert.Contains("stash apply 0", git.Calls);
        h.Press(Key.CursorDown, Key.O);
        Assert.Contains("stash pop 1", git.Calls);

        h.Prompts.ConfirmAnswer = true;
        h.Press(Key.D);
        Assert.Contains("stash drop 1", git.Calls);

        h.Prompts.StashAnswer = ("my work", true);
        h.Press(Key.N);
        Assert.Contains("stash push my work", git.Calls);
        Assert.Equal("stash show -p --include-untracked --no-color stash@{1}", git.Calls[^1]);
    }

    [Fact]
    public void OutsideARepositoryOffersInit()
    {
        var git = new FakeGitService { Status = null };
        using var h = Harness.Open(git);

        var screen = h.Screen();
        Assert.Contains("Not a git repository", screen, StringComparison.Ordinal);
        Assert.Contains("Initialize repository here", screen, StringComparison.Ordinal);

        h.Prompts.ConfirmAnswer = true;
        h.Press(Key.I);
        Assert.Equal("init", git.Calls[^1]);
    }

    [Fact]
    public void MissingGitIsReported()
    {
        using var h = Harness.Open(new FakeGitService { IsGitAvailable = false });
        Assert.Contains("git was not found", h.Screen(), StringComparison.Ordinal);
    }

    [Fact]
    public void CommitDialogCommitsTheTypedMessage()
    {
        var git = new FakeGitService { Status = SampleStatus() };
        using var h = Harness.Open(git, realPrompts: true);

        h.WhenDialog(Key.F, Key.I, Key.X, Key.S.WithCtrl);
        h.Press(Key.C);
        Assert.Equal("commit fix", git.Calls[^1]);

        h.WhenDialog(Key.Tab, Key.Space, Key.S.WithCtrl);
        h.Press(Key.C);
        Assert.Equal("amend ", git.Calls[^1]);

        var before = git.Calls.Count;
        h.WhenDialog(Key.A, Key.Esc);
        h.Press(Key.C);
        Assert.Equal(before, git.Calls.Count);
    }

    [Fact]
    public async Task PluginRegistersPanelAndPkGit()
    {
        using var t = TestPickle.Create();
        var git = new FakeGitService { Status = SampleStatus() };
        var host = new FakePanelHost();
        t.Runtime.ServiceRegistry.Add<IGitService>(git);
        t.Runtime.ServiceRegistry.Add<IPanelHost>(host);
        new GitPanelPlugin().Initialize(t.Runtime);

        var panel = t.Runtime.Panels.Get("git");
        Assert.NotNull(panel);
        Assert.Equal("Git", panel.Title);
        Assert.Equal("Alt+G", panel.DefaultKey);
        using (var view = (GitPanel)panel.CreateView(new PanelContext { Pickle = t.Runtime, Argument = "/somewhere" }))
        {
            Assert.Equal("/somewhere", view.WorkingDirectory);
        }

        var command = t.Runtime.Commands.Get("git");
        Assert.NotNull(command);
        var objects = new List<object?>();
        var errors = new List<string>();
        var ctx = new PickleCommandContext
        {
            Pickle = t.Runtime,
            WriteObject = objects.Add,
            WriteHost = _ => { },
            WriteError = errors.Add,
            Confirm = (_, d) => d,
            Cwd = "/repo",
        };

        Assert.Equal(0, await command.ExecuteAsync(ctx, [], CancellationToken.None));
        Assert.Equal(("git", "/repo"), (host.Shown[0].PanelId, host.Shown[0].Argument));

        Assert.Equal(0, await command.ExecuteAsync(ctx, ["status"], CancellationToken.None));
        Assert.Same(git.Status, Assert.Single(objects));

        git.Status = null;
        Assert.Equal(1, await command.ExecuteAsync(ctx, ["status"], CancellationToken.None));
        Assert.Equal(2, await command.ExecuteAsync(ctx, ["bogus"], CancellationToken.None));
        Assert.Equal(2, errors.Count);
    }

    private sealed class FakePrompts : IGitPanelPrompts
    {
        public bool ConfirmAnswer { get; set; }
        public string? TextAnswer { get; set; }
        public (string, bool)? StashAnswer { get; set; }
        public List<string> Confirms { get; } = [];
        public List<(string Title, string Message)> Infos { get; } = [];
        public List<string> Errors { get; } = [];

        public bool Confirm(string title, string message)
        {
            Confirms.Add(message);
            return ConfirmAnswer;
        }

        public void Info(string title, string message) => Infos.Add((title, message));

        public void Error(string message) => Errors.Add(message);

        public string? AskText(string title, string label, string initial = "") => TextAnswer;

        public (string Message, bool Amend)? AskCommit() => null;

        public (string Message, bool IncludeUntracked)? AskStash() => StashAnswer;
    }

    private sealed class Harness : IDisposable
    {
        private readonly SessionToken? _token;
        private Key[] _dialogKeys = [];

        private Harness(TestPickle pickle, IApplication app, GitPanel panel, FakePrompts prompts)
        {
            Pickle = pickle;
            App = app;
            Panel = panel;
            Prompts = prompts;
            _token = app.Begin(panel);
            app.Iteration += (_, _) =>
            {
                if (_dialogKeys.Length > 0 && app.TopRunnableView is Dialog)
                {
                    var keys = _dialogKeys;
                    _dialogKeys = [];
                    foreach (var key in keys)
                    {
                        app.InjectKey(key);
                    }
                }
            };
            Pump();
        }

        public TestPickle Pickle { get; }
        public IApplication App { get; }
        public GitPanel Panel { get; }
        public FakePrompts Prompts { get; }

        public static Harness Open(FakeGitService git, bool realPrompts = false)
        {
            var pickle = TestPickle.Create();
            pickle.Runtime.ServiceRegistry.Add<IGitService>(git);
            var app = Application.Create(new VirtualTimeProvider());
            app.Init();
            var panel = new GitPanel(new PanelContext { Pickle = pickle.Runtime, Argument = "/repo" });
            var prompts = new FakePrompts();
            if (!realPrompts)
            {
                panel.Prompts = prompts;
            }

            return new Harness(pickle, app, panel, prompts);
        }

        /// <summary>Keys to type into the next modal dialog once it is showing.</summary>
        public void WhenDialog(params Key[] keys) => _dialogKeys = keys;

        public void Press(params Key[] keys)
        {
            foreach (var key in keys)
            {
                App.InjectKey(key);
                Pump();
            }
        }

        public string Screen()
        {
            App.LayoutAndDraw(true);
            return App.Driver!.ToString() ?? string.Empty;
        }

        public Terminal.Gui.Drawing.Color? ForegroundAt(string text)
        {
            var lines = Screen().Split('\n');
            for (var y = 0; y < lines.Length; y++)
            {
                var x = lines[y].IndexOf(text, StringComparison.Ordinal);
                if (x >= 0)
                {
                    return App.Driver!.Contents![y, x].Attribute?.Foreground;
                }
            }

            throw new InvalidOperationException($"'{text}' is not on screen");
        }

        public void Dispose()
        {
            if (_token is not null)
            {
                App.End(_token);
            }

            Panel.Dispose();
            App.Dispose();
            Pickle.Dispose();
        }

        private void Pump()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                App.TimedEvents!.RunTimers();
                if (Panel.PendingOperations == 0)
                {
                    App.TimedEvents!.RunTimers();
                    return;
                }

                Thread.Sleep(2);
            }

            throw new TimeoutException("git panel background work did not finish");
        }
    }
}
