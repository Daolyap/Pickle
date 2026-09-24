using System.Text;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Git;

/// <summary>
/// Alt+G: status tree + colored diff with file, hunk and line staging, commit, branches, log and stash views,
/// fetch/pull/push. All git work goes through <see cref="IGitService"/> off the UI thread.
/// </summary>
public sealed partial class GitPanel : PanelWindow
{
    private const string StatusHelp = "Space stage/unstage · s/u hunk · v mark line · [ ] hunk · d discard · c commit · Tab diff";

    private readonly IGitService? _git;
    private readonly Label _header;
    private readonly Label _help;
    private readonly GitLinesView _list;
    private readonly GitLinesView _detail;
    private readonly Button _init;
    private readonly HashSet<DiffTag> _marked = [];
    private GitMode _mode = GitMode.Status;
    private GitStatus? _status;
    private IReadOnlyList<DiffFile> _diff = [];
    private bool _diffStageable;
    private int _detailRequest;
    private int _pending;
    private bool _started;

    public GitPanel(PanelContext context)
        : base(context, "Git")
    {
        _git = context.Pickle.Services.Get<IGitService>();
        WorkingDirectory = string.IsNullOrWhiteSpace(context.Argument) ? context.Pickle.Shell.CurrentDirectory : context.Argument;
        var colors = context.Pickle.Themes.Current.Ui;

        _header = new Label { X = 0, Y = 0, Width = Dim.Fill(), Text = "Loading…" };
        _help = new Label { X = 0, Y = 1, Width = Dim.Fill(), Text = StatusHelp };
        _list = new GitLinesView(colors) { X = 0, Y = 2, Width = Dim.Percent(38), Height = Dim.Fill() };
        _detail = new GitLinesView(colors) { X = Pos.Right(_list) + 1, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(), Gutter = DetailGutter };
        _init = new Button { Text = "_Initialize repository here", X = 2, Y = 7, Visible = false };
        _init.Accepting += (_, e) =>
        {
            InitRepository();
            e.Handled = true;
        };
        Body.Add(_header, _help, _list, _detail, _init);
        _list.CursorChanged += LoadDetail;
        _detail.CursorChanged += () => _detail.SetNeedsDraw();

        AddHint(Key.Space, "Stage", ToggleStage);
        AddHint(Key.S, "Hunk+", () => StageHunk(forward: true));
        AddHint(Key.U, "Hunk−", () => StageHunk(forward: false));
        AddHint(Key.C, "Commit", Commit);
        AddHint(Key.D, "Discard", Delete);
        AddHint(Key.B, "Branches", () => SetMode(GitMode.Branches));
        AddHint(Key.L, "Log", () => SetMode(GitMode.Log));
        AddHint(Key.Z, "Stash", () => SetMode(GitMode.Stash));
        AddHint(Key.F, "Fetch", Fetch);
        AddHint(Key.P, "Pull", Pull);
        AddHint(Key.P.WithShift, "Push", Push);
        AddHint(Key.S.WithShift, "All+", StageAll);
        AddHint(Key.U.WithShift, "All−", UnstageAll);
        AddHint(Key.F5, "Refresh", Refresh);

        Prompts = new DialogPrompts(this);
    }

    private enum GitMode
    {
        Status,
        Branches,
        Log,
        Stash,
    }

    private enum Section
    {
        Conflicts,
        Staged,
        Changes,
        Untracked,
    }

    /// <summary>The directory whose repository is shown (the shell's current directory by default).</summary>
    public string WorkingDirectory { get; }

    internal IGitPanelPrompts Prompts { get; set; }

    /// <summary>Background git calls still running (tests wait for zero).</summary>
    internal int PendingOperations => _pending;

    private string Root => _status?.Root ?? WorkingDirectory;

    protected override void OnIsRunningChanged(bool newIsRunning)
    {
        base.OnIsRunningChanged(newIsRunning);
        if (newIsRunning && !_started)
        {
            _started = true;
            _list.SetFocus();
            Refresh();
        }
    }

    protected override bool OnKeyDown(Key key)
    {
        if (key == Key.Esc && _mode != GitMode.Status)
        {
            SetMode(GitMode.Status);
            return true;
        }

        if (ActionFor(key) is { } action)
        {
            action();
            return true;
        }

        return base.OnKeyDown(key);
    }

    private Action? ActionFor(Key key)
    {
        if (key == Key.Space)
        {
            return ToggleStage;
        }

        if (key == Key.Enter)
        {
            return Activate;
        }

        if (key == Key.F5)
        {
            return Refresh;
        }

        if (key.IsCtrl || key.IsAlt)
        {
            return null;
        }

        var c = key.AsRune.Value;
        return c switch
        {
            's' => () => StageHunk(forward: true),
            'u' => () => StageHunk(forward: false),
            'S' => StageAll,
            'U' => UnstageAll,
            'd' => Delete,
            'c' => Commit,
            'b' => () => SetMode(GitMode.Branches),
            'l' => () => SetMode(GitMode.Log),
            'z' => () => SetMode(GitMode.Stash),
            'f' => Fetch,
            'p' => Pull,
            'P' => Push,
            '[' => () => JumpHunk(-1),
            ']' => () => JumpHunk(1),
            'v' => ToggleMark,
            'n' when _mode == GitMode.Branches => NewBranch,
            'n' when _mode == GitMode.Stash => StashPush,
            'a' when _mode == GitMode.Stash => () => StashApply(pop: false),
            'o' when _mode == GitMode.Stash => () => StashApply(pop: true),
            'i' when _status is null => InitRepository,
            _ => null,
        };
    }

    // ───────────────────────────── loading ─────────────────────────────

    private void Refresh()
    {
        if (_git is null || !_git.IsGitAvailable)
        {
            ShowMessage(_git is null ? "The git service is not available." : "git was not found. Install Git and make sure it is on PATH.");
            return;
        }

        var git = _git;
        var mode = _mode;
        var directory = WorkingDirectory;
        Background(
            async ct =>
            {
                var status = await git.GetStatusAsync(directory, ct).ConfigureAwait(false);
                object? extra = status is null ? null : mode switch
                {
                    GitMode.Branches => await git.GetBranchesAsync(status.Root, includeRemote: true, ct).ConfigureAwait(false),
                    GitMode.Log => await git.GetLogAsync(status.Root, 300, graph: true, ct).ConfigureAwait(false),
                    GitMode.Stash => await git.GetStashesAsync(status.Root, ct).ConfigureAwait(false),
                    _ => null,
                };
                return (status, extra);
            },
            r => Apply(mode, r.status, r.extra),
            "loading…");
    }

    private void Apply(GitMode mode, GitStatus? status, object? extra)
    {
        _status = status;
        _header.Text = HeaderText(status);
        if (status is null)
        {
            _help.Text = "i initialize · Esc close";
            _list.SetRows(
            [
                new GitRow("Not a git repository:", GitRowStyle.Warning, Selectable: false),
                new GitRow(WorkingDirectory, GitRowStyle.Muted, Selectable: false),
            ]);
            ShowDetail([]);
            _init.Visible = true;
            _init.SetFocus();
            return;
        }

        _init.Visible = false;
        if (mode != _mode)
        {
            return;
        }

        switch (mode)
        {
            case GitMode.Status:
                ShowStatus(status);
                break;
            case GitMode.Branches:
                ShowBranches(extra as IReadOnlyList<GitBranch> ?? []);
                break;
            case GitMode.Log:
                ShowLog(extra as IReadOnlyList<GitCommit> ?? []);
                break;
            case GitMode.Stash:
                ShowStashes(extra as IReadOnlyList<GitStash> ?? []);
                break;
        }
    }

    private void ShowMessage(string message)
    {
        _header.Text = message;
        _list.SetRows([new GitRow(message, GitRowStyle.Warning, Selectable: false)]);
        ShowDetail([]);
    }

    private static string HeaderText(GitStatus? status)
    {
        if (status is null)
        {
            return "Not a git repository";
        }

        var sb = new StringBuilder("⎇ ");
        sb.Append(status.IsDetached ? "detached @ " + (status.HeadSha is { Length: > 7 } sha ? sha[..7] : status.HeadSha) : status.Branch ?? "?");
        if (status.Upstream is not null)
        {
            sb.Append(" → ").Append(status.Upstream);
        }

        if (status.Ahead > 0)
        {
            sb.Append(" ↑").Append(status.Ahead);
        }

        if (status.Behind > 0)
        {
            sb.Append(" ↓").Append(status.Behind);
        }

        if (status.Operation is not null)
        {
            sb.Append("  ⚠ ").Append(status.Operation.ToUpperInvariant()).Append(" in progress");
        }

        if (status.StashCount > 0)
        {
            sb.Append("  ≡ ").Append(status.StashCount).Append(status.StashCount == 1 ? " stash" : " stashes");
        }

        return sb.Append("   ").Append(status.Root).ToString();
    }

    private void SetMode(GitMode mode)
    {
        _mode = _mode == mode && mode != GitMode.Status ? GitMode.Status : mode;
        _help.Text = _mode switch
        {
            GitMode.Branches => "Enter checkout · n new branch · d delete · Esc back",
            GitMode.Log => "↑↓ select commit · Tab details · Esc back",
            GitMode.Stash => "Enter/a apply · o pop · n new stash · d drop · Esc back",
            _ => StatusHelp,
        };
        _list.SetRows([]);
        ShowDetail([]);
        _list.SetFocus();
        Refresh();
    }

    private void Background<T>(Func<CancellationToken, Task<T>> work, Action<T> onDone, string busyText)
    {
        _pending++;
        RunInBackground(
            async ct =>
            {
                try
                {
                    return await work(ct).ConfigureAwait(false);
                }
                catch
                {
                    OnUi(() => _pending--);
                    throw;
                }
            },
            result =>
            {
                try
                {
                    onDone(result);
                }
                finally
                {
                    _pending--;
                }
            },
            busyText);
    }

    /// <summary>Runs a git action, reports failures, and refreshes afterwards.</summary>
    private void RunAction(Func<IGitService, CancellationToken, Task<GitCommandResult>> work, string busyText, Action<GitCommandResult>? onSuccess = null)
    {
        if (_git is not { } git)
        {
            return;
        }

        Background(
            ct => work(git, ct),
            result =>
            {
                if (result.Success)
                {
                    onSuccess?.Invoke(result);
                }
                else
                {
                    Prompts.Error(Describe(result));
                }

                Refresh();
            },
            busyText);
    }

    private static string Describe(GitCommandResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        text = text.Trim();
        return text.Length > 0 ? Clip(text) : $"git failed (exit code {result.ExitCode}).";
    }

    private static string Clip(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return lines.Length <= 20 ? string.Join('\n', lines) : string.Join('\n', lines.Take(20)) + "\n…";
    }

    // ───────────────────────────── status ─────────────────────────────

    private sealed record StatusItem(GitStatusEntry Entry, Section Section);

    private void ShowStatus(GitStatus status)
    {
        var previous = _list.Current?.Tag as StatusItem;
        var previousIndex = _list.SelectedIndex;
        var rows = new List<GitRow>();
        AddSection(rows, "Conflicts", Section.Conflicts, status.Entries.Where(e => e.IsConflicted));
        AddSection(rows, "Staged", Section.Staged, status.Entries.Where(e => e.IsStaged && !e.IsConflicted));
        AddSection(rows, "Changes", Section.Changes, status.Entries.Where(e => !e.IsConflicted && !e.IsUntracked && e.WorktreeStatus is not (GitChangeKind.None or GitChangeKind.Ignored)));
        AddSection(rows, "Untracked", Section.Untracked, status.Entries.Where(e => e.IsUntracked));
        if (rows.Count == 0)
        {
            rows.Add(new GitRow("Nothing to commit, working tree clean.", GitRowStyle.Muted, Selectable: false));
        }

        var cursor = previous is null ? -1 : rows.FindIndex(r => r.Tag is StatusItem s && s.Section == previous.Section && s.Entry.Path == previous.Entry.Path);
        if (cursor < 0 && previous is not null)
        {
            cursor = rows.FindIndex(r => r.Tag is StatusItem s && s.Entry.Path == previous.Entry.Path);
        }

        _list.SetRows(rows, cursor >= 0 ? cursor : Math.Max(0, previousIndex));
    }

    private static void AddSection(List<GitRow> rows, string title, Section section, IEnumerable<GitStatusEntry> entries)
    {
        var list = entries.ToList();
        if (list.Count == 0)
        {
            return;
        }

        rows.Add(new GitRow($"{title} ({list.Count})", GitRowStyle.Header, Selectable: false));
        var style = section switch
        {
            Section.Conflicts => GitRowStyle.Conflict,
            Section.Staged => GitRowStyle.Added,
            Section.Changes => GitRowStyle.Removed,
            _ => GitRowStyle.Warning,
        };
        foreach (var entry in list)
        {
            var kind = section == Section.Staged ? entry.IndexStatus : entry.WorktreeStatus;
            var path = entry.OriginalPath is null ? entry.Path : $"{entry.OriginalPath} → {entry.Path}";
            rows.Add(new GitRow($"  {Code(kind)} {path}", style, new StatusItem(entry, section)));
        }
    }

    private static char Code(GitChangeKind kind) => kind switch
    {
        GitChangeKind.Modified => 'M',
        GitChangeKind.Added => 'A',
        GitChangeKind.Deleted => 'D',
        GitChangeKind.Renamed => 'R',
        GitChangeKind.Copied => 'C',
        GitChangeKind.TypeChanged => 'T',
        GitChangeKind.Unmerged => 'U',
        GitChangeKind.Untracked => '?',
        _ => ' ',
    };

    private static IReadOnlyList<string> PathsOf(GitStatusEntry entry) =>
        entry.OriginalPath is null ? [entry.Path] : [entry.Path, entry.OriginalPath];

    private void ToggleStage()
    {
        if (_mode != GitMode.Status)
        {
            Activate();
            return;
        }

        if (_list.Current?.Tag is not StatusItem item)
        {
            return;
        }

        var root = Root;
        if (item.Section == Section.Staged)
        {
            RunAction((g, ct) => g.UnstageAsync(root, PathsOf(item.Entry), ct), "unstaging…");
        }
        else
        {
            RunAction((g, ct) => g.StageAsync(root, PathsOf(item.Entry), ct), "staging…");
        }
    }

    private void StageAll()
    {
        if (_status is not null)
        {
            var root = Root;
            RunAction((g, ct) => g.StageAsync(root, ["."], ct), "staging…");
        }
    }

    private void UnstageAll()
    {
        if (_status is not null)
        {
            var root = Root;
            RunAction((g, ct) => g.UnstageAsync(root, ["."], ct), "unstaging…");
        }
    }

    private void Delete()
    {
        switch (_mode)
        {
            case GitMode.Branches:
                DeleteBranch();
                break;
            case GitMode.Stash:
                StashDrop();
                break;
            case GitMode.Status:
                Discard();
                break;
        }
    }

    private void Discard()
    {
        if (_list.Current?.Tag is not StatusItem item)
        {
            return;
        }

        var root = Root;
        var paths = PathsOf(item.Entry);
        switch (item.Section)
        {
            case Section.Conflicts:
                Prompts.Error("Resolve the conflict (or abort the operation) instead of discarding.");
                break;
            case Section.Staged:
                if (Prompts.Confirm("Discard changes", $"Discard ALL changes (staged and unstaged) to {item.Entry.Path}?\nThis cannot be undone."))
                {
                    RunAction(
                        async (g, ct) =>
                        {
                            var unstage = await g.UnstageAsync(root, paths, ct).ConfigureAwait(false);
                            return unstage.Success ? await g.DiscardAsync(root, paths, ct).ConfigureAwait(false) : unstage;
                        },
                        "discarding…");
                }

                break;
            default:
                var what = item.Section == Section.Untracked ? $"Delete untracked {item.Entry.Path}?" : $"Discard unstaged changes to {item.Entry.Path}?";
                if (Prompts.Confirm("Discard changes", what + "\nThis cannot be undone."))
                {
                    RunAction((g, ct) => g.DiscardAsync(root, paths, ct), "discarding…");
                }

                break;
        }
    }

    private void Commit()
    {
        if (_status is null || Prompts.AskCommit() is not { } request)
        {
            return;
        }

        var root = Root;
        RunAction((g, ct) => g.CommitAsync(root, request.Message, request.Amend, ct), "committing…");
    }

    private void Activate()
    {
        switch (_mode)
        {
            case GitMode.Branches:
                Checkout();
                break;
            case GitMode.Stash:
                StashApply(pop: false);
                break;
            default:
                if (_list.HasFocus && _detail.Rows.Count > 0)
                {
                    _detail.SetFocus();
                }

                break;
        }
    }

    private void InitRepository()
    {
        if (_status is not null || _git is null)
        {
            return;
        }

        var directory = WorkingDirectory;
        if (Prompts.Confirm("Initialize repository", $"Run git init in {directory}?"))
        {
            RunAction((g, ct) => g.RunAsync(directory, ["init"], ct), "initializing…");
        }
    }

    private void Fetch() => RunNetwork("Fetch", "fetching…", (g, root, ct) => g.FetchAsync(root, ct));

    private void Pull() => RunNetwork("Pull", "pulling…", (g, root, ct) => g.PullAsync(root, ct));

    private void Push()
    {
        if (_status is null)
        {
            return;
        }

        var setUpstream = false;
        if (_status is { Upstream: null, IsDetached: false, Branch: { } branch })
        {
            if (!Prompts.Confirm("Push", $"'{branch}' has no upstream branch. Push it and set the upstream?"))
            {
                return;
            }

            setUpstream = true;
        }

        RunNetwork("Push", "pushing…", (g, root, ct) => g.PushAsync(root, setUpstream, ct));
    }

    private void RunNetwork(string title, string busyText, Func<IGitService, string, CancellationToken, Task<GitCommandResult>> work)
    {
        if (_status is null)
        {
            return;
        }

        var root = Root;
        RunAction(
            (g, ct) => work(g, root, ct),
            busyText,
            r =>
            {
                var text = (r.Output + "\n" + r.Error).Trim();
                Prompts.Info(title, text.Length == 0 ? "Done." : Clip(text));
            });
    }
}
