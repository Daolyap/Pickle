using System.Globalization;
using Pickle.Abstractions.Services;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Git;

public sealed partial class GitPanel
{
    /// <summary>Position of a diff row: file, hunk (-1 = file header) and line within the hunk (-1 = hunk header).</summary>
    private sealed record DiffTag(int File, int Hunk, int Line);

    // ───────────────────────────── detail pane ─────────────────────────────

    private void LoadDetail()
    {
        var request = ++_detailRequest;
        var root = Root;
        switch (_mode)
        {
            case GitMode.Status when _list.Current?.Tag is StatusItem item && _git is { } git:
                var staged = item.Section == Section.Staged;
                Background(ct => git.GetDiffAsync(root, item.Entry.Path, staged, ct), diff =>
                {
                    if (request == _detailRequest)
                    {
                        ShowDiff(diff, stageable: item.Section != Section.Conflicts);
                    }
                }, "diff…");
                break;
            case GitMode.Log when _list.Current?.Tag is GitCommit commit && _git is { } git && IsHex(commit.Sha):
                Background(ct => git.RunAsync(root, ["show", "--stat", "--format=fuller", commit.Sha], ct), result =>
                {
                    if (request == _detailRequest)
                    {
                        ShowText(result.Success ? result.Output : Describe(result));
                    }
                }, "show…");
                break;
            case GitMode.Stash when _list.Current?.Tag is GitStash stash && _git is { } git:
                var name = "stash@{" + stash.Index.ToString(CultureInfo.InvariantCulture) + "}";
                Background(ct => git.RunAsync(root, ["stash", "show", "-p", "--include-untracked", "--no-color", name], ct), result =>
                {
                    if (request == _detailRequest)
                    {
                        ShowDiff(result.Success ? result.Output : string.Empty, stageable: false);
                    }
                }, "stash…");
                break;
            case GitMode.Branches when _list.Current?.Tag is GitBranch branch:
                ShowDetail(BranchDetails(branch));
                break;
            default:
                ShowDetail([]);
                break;
        }
    }

    private void ShowDetail(IReadOnlyList<GitRow> rows)
    {
        _diff = [];
        _diffStageable = false;
        _marked.Clear();
        _detail.SetRows(rows);
    }

    private void ShowText(string text)
    {
        var rows = text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n')
            .Select((line, i) => new GitRow(line, i == 0 ? GitRowStyle.Header : line.StartsWith(' ') ? GitRowStyle.Normal : GitRowStyle.Muted))
            .ToList();
        ShowDetail(rows);
    }

    private void ShowDiff(string text, bool stageable)
    {
        var files = DiffParser.Parse(text);
        var rows = new List<GitRow>();
        var firstHunk = -1;
        for (var f = 0; f < files.Count; f++)
        {
            var file = files[f];
            var suffix = file.IsNewFile ? "  (new file)" : file.IsDeletedFile ? "  (deleted)" : string.Empty;
            if (file.OldPath is not null && file.NewPath is not null && file.OldPath != file.NewPath)
            {
                suffix = $"  (renamed from {file.OldPath})";
            }

            rows.Add(new GitRow(file.Path + suffix, GitRowStyle.Header, new DiffTag(f, -1, -1)));
            if (file.IsBinary)
            {
                rows.Add(new GitRow("Binary file (stage or unstage the whole file with Space)", GitRowStyle.Muted, new DiffTag(f, -1, -1)));
            }

            for (var h = 0; h < file.Hunks.Count; h++)
            {
                var hunk = file.Hunks[h];
                if (firstHunk < 0)
                {
                    firstHunk = rows.Count;
                }

                rows.Add(new GitRow(hunk.Header, GitRowStyle.Hunk, new DiffTag(f, h, -1)));
                for (var l = 0; l < hunk.Lines.Count; l++)
                {
                    var line = hunk.Lines[l];
                    var style = line.Kind switch
                    {
                        DiffLineKind.Added => GitRowStyle.Added,
                        DiffLineKind.Removed => GitRowStyle.Removed,
                        DiffLineKind.NoNewline => GitRowStyle.Muted,
                        _ => GitRowStyle.Normal,
                    };
                    rows.Add(new GitRow(line.Text, style, new DiffTag(f, h, l)));
                }
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(new GitRow(string.IsNullOrWhiteSpace(text) ? "No changes to show." : text.Trim(), GitRowStyle.Muted, Selectable: false));
        }

        _diff = files;
        _diffStageable = stageable;
        _marked.Clear();
        _detail.SetRows(rows, Math.Max(0, firstHunk));
    }

    /// <summary>The hunk under the diff cursor (a file header selects that file's first hunk).</summary>
    private (DiffFile File, int Hunk)? CurrentHunk()
    {
        if (_detail.Current?.Tag is not DiffTag tag || tag.File >= _diff.Count)
        {
            return null;
        }

        var file = _diff[tag.File];
        if (file.Hunks.Count == 0)
        {
            return null;
        }

        return (file, Math.Max(0, tag.Hunk));
    }

    private string? DetailGutter(int row)
    {
        if (_mode != GitMode.Status || row >= _detail.Rows.Count || _detail.Rows[row].Tag is not DiffTag tag)
        {
            return null;
        }

        if (_marked.Contains(tag))
        {
            return "●";
        }

        return tag.Hunk >= 0 && _detail.Current?.Tag is DiffTag current && current.File == tag.File && Math.Max(0, current.Hunk) == tag.Hunk
            ? "▌"
            : null;
    }

    private void JumpHunk(int direction)
    {
        var rows = _detail.Rows;
        for (var i = _detail.SelectedIndex + direction; i >= 0 && i < rows.Count; i += direction)
        {
            if (rows[i].Tag is DiffTag { Hunk: >= 0, Line: -1 })
            {
                _detail.MoveTo(i, preferForward: direction > 0);
                _detail.ScrollTo(i);
                return;
            }
        }
    }

    private void ToggleMark()
    {
        if (_mode != GitMode.Status || _detail.Current?.Tag is not DiffTag { Line: >= 0 } tag || tag.File >= _diff.Count)
        {
            return;
        }

        if (!_diff[tag.File].Hunks[tag.Hunk].Lines[tag.Line].IsChange)
        {
            return;
        }

        if (!_marked.Remove(tag))
        {
            _marked.Add(tag);
        }

        _detail.MoveTo(_detail.SelectedIndex + 1);
        _detail.SetNeedsDraw();
    }

    /// <summary><c>s</c> stages (forward) and <c>u</c> unstages (reverse) the marked lines, or else the current hunk.</summary>
    private void StageHunk(bool forward)
    {
        if (_mode != GitMode.Status || _list.Current?.Tag is not StatusItem item)
        {
            return;
        }

        if (item.Section == Section.Conflicts || !_diffStageable)
        {
            Prompts.Error("Resolve the conflict, then stage the whole file with Space.");
            return;
        }

        if ((item.Section == Section.Staged) == forward)
        {
            Prompts.Error(forward ? "Select a file under Changes or Untracked to stage hunks." : "Select a file under Staged to unstage hunks.");
            return;
        }

        var patches = BuildPatches(reverse: !forward, out var problem);
        if (patches.Count == 0)
        {
            Prompts.Error(problem ?? "Nothing selected.");
            return;
        }

        var root = Root;
        RunAction(
            async (g, ct) =>
            {
                GitCommandResult last = new(true, string.Empty, string.Empty, 0);
                foreach (var patch in patches)
                {
                    last = await g.ApplyPatchToIndexAsync(root, patch, reverse: !forward, ct).ConfigureAwait(false);
                    if (!last.Success)
                    {
                        break;
                    }
                }

                return last;
            },
            forward ? "staging…" : "unstaging…");
    }

    private List<string> BuildPatches(bool reverse, out string? problem)
    {
        problem = null;
        var patches = new List<string>();
        if (_marked.Count > 0)
        {
            // Bottom-up, so applying one hunk never shifts the line numbers of those still to apply.
            foreach (var group in _marked.GroupBy(t => (t.File, t.Hunk)).OrderByDescending(g => g.Key.File).ThenByDescending(g => g.Key.Hunk))
            {
                var file = _diff[group.Key.File];
                if (!file.CanStagePartially)
                {
                    continue;
                }

                var selected = group.Select(t => t.Line).ToHashSet();
                if (DiffParser.BuildLinePatch(file, file.Hunks[group.Key.Hunk], selected, reverse) is { } patch)
                {
                    patches.Add(patch);
                }
            }

            return patches;
        }

        if (CurrentHunk() is not { } current)
        {
            problem = "No hunk selected (use [ and ] to pick one).";
            return patches;
        }

        if (!current.File.CanStagePartially)
        {
            problem = "This diff can't be staged in parts; use Space to stage the whole file.";
            return patches;
        }

        patches.Add(DiffParser.BuildHunkPatch(current.File, current.File.Hunks[current.Hunk]));
        return patches;
    }

    private static bool IsHex(string value) => value.Length is >= 4 and <= 64 && value.All(char.IsAsciiHexDigit);

    // ───────────────────────────── branches ─────────────────────────────

    private void ShowBranches(IReadOnlyList<GitBranch> branches)
    {
        var previous = (_list.Current?.Tag as GitBranch)?.Name;
        var rows = new List<GitRow>();
        var local = branches.Where(b => !b.IsRemote).ToList();
        var remote = branches.Where(b => b.IsRemote).ToList();
        if (local.Count > 0)
        {
            rows.Add(new GitRow($"Local ({local.Count})", GitRowStyle.Header, Selectable: false));
            rows.AddRange(local.Select(b => new GitRow(
                (b.IsCurrent ? "* " : "  ") + b.Name + (b.Upstream is null ? string.Empty : "  → " + b.Upstream),
                b.IsCurrent ? GitRowStyle.Added : GitRowStyle.Normal,
                b)));
        }

        if (remote.Count > 0)
        {
            rows.Add(new GitRow($"Remote ({remote.Count})", GitRowStyle.Header, Selectable: false));
            rows.AddRange(remote.Select(b => new GitRow("  " + b.Name, GitRowStyle.Muted, b)));
        }

        if (rows.Count == 0)
        {
            rows.Add(new GitRow("No branches yet (make the first commit).", GitRowStyle.Muted, Selectable: false));
        }

        var cursor = previous is null ? -1 : rows.FindIndex(r => r.Tag is GitBranch b && b.Name == previous);
        _list.SetRows(rows, Math.Max(0, cursor));
    }

    private static List<GitRow> BranchDetails(GitBranch branch)
    {
        var rows = new List<GitRow> { new(branch.Name + (branch.IsCurrent ? "  (checked out)" : string.Empty), GitRowStyle.Header) };
        if (branch.Upstream is not null)
        {
            rows.Add(new GitRow("Upstream: " + branch.Upstream));
        }

        if (branch.LastCommitSubject is not null)
        {
            rows.Add(new GitRow("Last commit: " + branch.LastCommitSubject));
        }

        if (branch.LastCommitDate is { } date)
        {
            rows.Add(new GitRow("Date: " + date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), GitRowStyle.Muted));
        }

        rows.Add(new GitRow(string.Empty, Selectable: false));
        rows.Add(new GitRow(branch.IsRemote ? "Enter creates a local tracking branch and checks it out." : "Enter checks the branch out.", GitRowStyle.Muted));
        return rows;
    }

    private void Checkout()
    {
        if (_list.Current?.Tag is not GitBranch branch || branch.IsCurrent)
        {
            return;
        }

        // For "origin/x" check out local "x": git switches to it if it exists, else creates it tracking the remote.
        var target = branch.IsRemote && branch.Name.IndexOf('/', StringComparison.Ordinal) is var slash and > 0
            ? branch.Name[(slash + 1)..]
            : branch.Name;
        var root = Root;
        RunAction((g, ct) => g.CheckoutAsync(root, target, create: false, ct), "checking out…");
    }

    private void NewBranch()
    {
        if (Prompts.AskText("New branch", "Name of the new branch (created from HEAD and checked out):") is not { } name
            || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var root = Root;
        RunAction((g, ct) => g.CheckoutAsync(root, name.Trim(), create: true, ct), "creating branch…");
    }

    private void DeleteBranch()
    {
        if (_list.Current?.Tag is not GitBranch branch || _git is not { } git)
        {
            return;
        }

        if (branch.IsRemote)
        {
            Prompts.Error("Remote branches can't be deleted from here.");
            return;
        }

        if (branch.IsCurrent)
        {
            Prompts.Error("You can't delete the branch that is checked out.");
            return;
        }

        if (!Prompts.Confirm("Delete branch", $"Delete branch '{branch.Name}'?"))
        {
            return;
        }

        var root = Root;
        Background(ct => git.DeleteBranchAsync(root, branch.Name, force: false, ct), result =>
        {
            if (!result.Success && result.Error.Contains("not fully merged", StringComparison.OrdinalIgnoreCase)
                && Prompts.Confirm("Delete branch", $"'{branch.Name}' is not fully merged; its commits may be lost.\nDelete it anyway?"))
            {
                RunAction((g, ct) => g.DeleteBranchAsync(root, branch.Name, force: true, ct), "deleting…");
                return;
            }

            if (!result.Success)
            {
                Prompts.Error(Describe(result));
            }

            Refresh();
        }, "deleting…");
    }

    // ───────────────────────────── log ─────────────────────────────

    private void ShowLog(IReadOnlyList<GitCommit> commits)
    {
        var previous = (_list.Current?.Tag as GitCommit)?.Sha;
        var rows = commits.Select(c =>
        {
            var refs = c.Refs.Count == 0 ? string.Empty : " (" + string.Join(", ", c.Refs) + ")";
            var graph = c.Graph.Length == 0 ? string.Empty : c.Graph + " ";
            return new GitRow($"{graph}{c.ShortSha}{refs} {c.Subject}", c.Refs.Count > 0 ? GitRowStyle.Warning : GitRowStyle.Normal, c);
        }).ToList();
        if (rows.Count == 0)
        {
            rows.Add(new GitRow("No commits yet.", GitRowStyle.Muted, Selectable: false));
        }

        var cursor = previous is null ? -1 : rows.FindIndex(r => r.Tag is GitCommit c && c.Sha == previous);
        _list.SetRows(rows, Math.Max(0, cursor));
    }

    // ───────────────────────────── stash ─────────────────────────────

    private void ShowStashes(IReadOnlyList<GitStash> stashes)
    {
        var rows = stashes.Select(s => new GitRow($"{s.Name}  {s.Message}", GitRowStyle.Normal, s)).ToList();
        if (rows.Count == 0)
        {
            rows.Add(new GitRow("No stashes. Press n to stash your changes.", GitRowStyle.Muted, Selectable: false));
        }

        _list.SetRows(rows, Math.Min(Math.Max(0, _list.SelectedIndex), rows.Count - 1));
    }

    private void StashPush()
    {
        if (Prompts.AskStash() is not { } request)
        {
            return;
        }

        var root = Root;
        RunAction((g, ct) => g.StashPushAsync(root, request.Message, request.IncludeUntracked, ct), "stashing…");
    }

    private void StashApply(bool pop)
    {
        if (_list.Current?.Tag is not GitStash stash)
        {
            return;
        }

        var root = Root;
        RunAction((g, ct) => g.StashApplyAsync(root, stash.Index, pop, ct), pop ? "popping…" : "applying…");
    }

    private void StashDrop()
    {
        if (_list.Current?.Tag is not GitStash stash || !Prompts.Confirm("Drop stash", $"Drop {stash.Name} ({stash.Message})?\nThis cannot be undone."))
        {
            return;
        }

        var root = Root;
        RunAction((g, ct) => g.StashDropAsync(root, stash.Index, ct), "dropping…");
    }

    // ───────────────────────────── dialogs ─────────────────────────────

    private sealed class DialogPrompts(GitPanel panel) : IGitPanelPrompts
    {
        public bool Confirm(string title, string message) => panel.Confirm(title, message);

        public void Info(string title, string message) => panel.ShowInfo(title, message);

        public void Error(string message) => panel.ShowError(message);

        public string? AskText(string title, string label, string initial = "")
        {
            if (panel.App is not { } app)
            {
                return null;
            }

            using var dialog = new Dialog { Title = title, Width = Dim.Percent(60) };
            var field = new TextField { X = 0, Y = 1, Width = Dim.Fill(), Text = initial };
            dialog.Add(new Label { X = 0, Y = 0, Text = label }, field);
            dialog.AddButton(new Button { Title = "_Cancel" });
            dialog.AddButton(new Button { Title = "_OK" });
            field.SetFocus();
            app.Run(dialog);
            return dialog.Result == 1 ? field.Text : null;
        }

        public (string Message, bool Amend)? AskCommit()
        {
            if (panel.App is not { } app)
            {
                return null;
            }

            using var dialog = new Dialog { Title = "Commit", Width = Dim.Percent(80) };
#pragma warning disable CS0618 // TextView is superseded by an external editor package; it is still the built-in multi-line input.
            var message = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = 8, TabKeyAddsTab = false };
#pragma warning restore CS0618
            var amend = new CheckBox { X = 0, Y = Pos.Bottom(message), Text = "_Amend previous commit (empty message keeps the old one)" };
            var hint = new Label { X = 0, Y = Pos.Bottom(amend), Text = "Ctrl+S commit · Tab to buttons · Esc cancel" };
            dialog.Add(message, amend, hint);
            dialog.AddButton(new Button { Title = "_Cancel" });
            dialog.AddButton(new Button { Title = "C_ommit" });
            var accepted = false;
            dialog.KeyDown += (_, key) =>
            {
                if (key == Key.S.WithCtrl)
                {
                    accepted = true;
                    dialog.RequestStop();
                    key.Handled = true;
                }
            };
            message.SetFocus();
            app.Run(dialog);
            if (!accepted && dialog.Result != 1)
            {
                return null;
            }

            return (message.Text, amend.Value == CheckState.Checked);
        }

        public (string Message, bool IncludeUntracked)? AskStash()
        {
            if (panel.App is not { } app)
            {
                return null;
            }

            using var dialog = new Dialog { Title = "Stash changes", Width = Dim.Percent(60) };
            var field = new TextField { X = 0, Y = 1, Width = Dim.Fill() };
            var untracked = new CheckBox { X = 0, Y = 3, Text = "Include _untracked files", Value = CheckState.Checked };
            dialog.Add(new Label { X = 0, Y = 0, Text = "Message (optional):" }, field, untracked);
            dialog.AddButton(new Button { Title = "_Cancel" });
            dialog.AddButton(new Button { Title = "_Stash" });
            field.SetFocus();
            app.Run(dialog);
            return dialog.Result == 1 ? (field.Text, untracked.Value == CheckState.Checked) : null;
        }
    }
}
