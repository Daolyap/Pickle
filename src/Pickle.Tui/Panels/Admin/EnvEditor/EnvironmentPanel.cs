using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui.Widgets;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Admin.EnvEditor;

/// <summary>
/// Hosts file, PATH, environment variables and .env files in one place (Alt+O). Edits are staged and shown as a diff;
/// Save (F10) applies them as one privileged write: a UAC prompt on Windows, sudo or polkit elsewhere, never an
/// administrator Pickle. User-scope variables and .env files need no privileges at all.
/// </summary>
internal sealed class EnvironmentPanel : PanelWindow, IEditorHost
{
    public const string PanelId = "environment";

    private readonly EditorMode[] _modes = [new HostsMode(), new PathMode(), new VariablesMode(), new DotEnvMode()];
    private readonly FilterableList<EditorRow> _list;
    private readonly PreviewPane _details;
    private readonly Label _tabs = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
    private int _mode;
    private EnvironmentScope _scope = EnvironmentScope.User;

    public static PanelDescriptor Descriptor { get; } = new()
    {
        Id = PanelId,
        Title = "Hosts, PATH & environment",
        Description = "Edit the hosts file, PATH, environment variables and .env files; privileged writes only when you save",
        DefaultKey = "Alt+O",
        CreateView = context => new EnvironmentPanel(context),
    };

    public EnvironmentPanel(PanelContext context)
        : base(context, "Hosts, PATH & environment")
    {
        Hosts = context.Pickle.Services.Require<IHostsService>();
        Variables = context.Pickle.Services.Require<IEnvironmentStore>();
        var preferred = Variables.Scopes.Contains(EnvironmentScope.User) ? EnvironmentScope.User : EnvironmentScope.Pickle;
        _scope = preferred;
        if (!string.IsNullOrEmpty(context.Argument))
        {
            var first = context.Argument.Split(' ', 2)[0];
            var index = Array.FindIndex(_modes, m => string.Equals(m.Title, first, StringComparison.OrdinalIgnoreCase) || (first.Equals("vars", StringComparison.OrdinalIgnoreCase) && m is VariablesMode) || (first.Equals("dotenv", StringComparison.OrdinalIgnoreCase) && m is DotEnvMode));
            if (index >= 0)
            {
                _mode = index;
            }

            if (context.Argument.Contains(' ', StringComparison.Ordinal))
            {
                DotEnvPath = context.Argument.Split(' ', 2)[1].Trim();
            }
        }

        _list = new FilterableList<EditorRow>(r => r.Text) { Y = 1, Width = Dim.Percent(55), Hint = r => r.Hint, Detail = r => r.Detail, ItemColor = Color, Schemes = Schemes };
        _details = new PreviewPane("Details") { X = Pos.Right(_list), Y = 1, Width = Dim.Fill(), Height = Dim.Fill(), Schemes = Schemes };
        Body.Add(_tabs, _list, _details);
        _list.SelectionChanged += (_, row) => ShowDetails(row);

        AddHint(Key.F2, "Add", () => Act((m, row, ct) => m.AddAsync(this, ct), needsRow: false));
        AddHint(Key.F3, "Edit", () => Act((m, row, ct) => m.EditAsync(this, row!, ct)));
        AddHint(Key.F4, "Delete", () => Act((m, row, ct) => m.DeleteAsync(this, row!, ct)));
        AddHint(Key.F6, "Up/On-off", () => Act((m, row, ct) => m.MoveOrToggleAsync(this, row!, -1, ct)));
        AddHint(Key.F7, "Down", () => Act((m, row, ct) => m.MoveOrToggleAsync(this, row!, +1, ct)));
        AddHint(Key.F8, "Tab", () => SwitchMode(+1));
        AddHint(Key.F9, "Scope", CycleScope);
        AddHint(Key.F10, "Save", Save);
        AddHint(Key.F5, "Reload", () => ReloadMode(askFirst: true));
        _list.Filter.SetFocus();
    }

    public IHostsService Hosts { get; }

    public IEnvironmentStore Variables { get; }

    public EnvironmentScope Scope => _scope;

    public string CurrentDirectory => Pickle.Shell.CurrentDirectory;

    public string? DotEnvPath { get; }

    internal EditorMode Mode => _modes[_mode];

    internal FilterableList<EditorRow> List => _list;

    internal PreviewPane Details => _details;

    /// <summary>Tests answer prompts and confirmations here instead of opening dialogs.</summary>
    internal Func<string, string, string?>? PromptHook { get; set; }

    internal Func<string, string, bool>? ConfirmHook { get; set; }

    internal void SelectMode(int index)
    {
        _mode = index;
        ReloadMode(askFirst: false);
    }

    public string? AskText(string title, string label, string initial = "")
    {
        if (PromptHook is { } hook)
        {
            return hook(title, label);
        }

        return OnUiBlocking(() => Prompt(title, label, initial));
    }

    public bool AskYesNo(string title, string message) =>
        ConfirmHook is { } hook ? hook(title, message) : OnUiBlocking(() => Confirm(title, message));

    protected override void OnOpened() => ReloadMode(askFirst: false);

    protected override void Restyle()
    {
        base.Restyle();
        _list.Refilter();
    }

    private T OnUiBlocking<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() => done.SetResult(work()));
        return done.Task.GetAwaiter().GetResult();
    }

    private Terminal.Gui.Drawing.Color? Color(EditorRow row) => row.Tone switch
    {
        RowTone.Muted => Schemes.Muted.Foreground,
        RowTone.Warning => Schemes.Warning.Foreground,
        RowTone.Error => Schemes.ErrorText.Foreground,
        RowTone.Good => Schemes.Success.Foreground,
        _ => null,
    };

    private void SwitchMode(int delta)
    {
        if (Mode.IsDirty && !AskYesNo("Unsaved changes", $"Discard the unsaved {Mode.Title} changes?"))
        {
            return;
        }

        _mode = (_mode + delta + _modes.Length) % _modes.Length;
        ReloadMode(askFirst: false);
    }

    private void CycleScope()
    {
        if (!Mode.UsesScope)
        {
            _details.ShowMessage("Scope", "The " + Mode.Title + " tab has no scope.");
            return;
        }

        if (Mode.IsDirty && !AskYesNo("Unsaved changes", "Discard the unsaved changes?"))
        {
            return;
        }

        var scopes = Variables.Scopes;
        _scope = scopes[(scopes.ToList().IndexOf(_scope) + 1) % scopes.Count];
        ReloadMode(askFirst: false);
    }

    private void ReloadMode(bool askFirst)
    {
        if (askFirst && Mode.IsDirty && !AskYesNo("Reload", "Discard the unsaved changes?"))
        {
            return;
        }

        var mode = Mode;
        RunInBackground(
            async ct =>
            {
                await mode.LoadAsync(this, ct).ConfigureAwait(false);
                return 0;
            },
            _ => Refresh(),
            "loading…");
    }

    private void Refresh()
    {
        var mode = Mode;
        _tabs.Text = string.Join("  ", _modes.Select(m => ReferenceEquals(m, mode) ? $"[{m.Title}]" : m.Title))
            + (mode.UsesScope ? $"   scope: {_scope}{(Variables.NeedsPrivileges(_scope) ? " (administrator)" : string.Empty)}" : string.Empty)
            + (mode.IsDirty ? "   ● unsaved" : string.Empty);
        var selected = _list.Selected?.Index;
        _list.SetItems(mode.Rows);
        if (selected is { } index && mode.Rows.FirstOrDefault(r => r.Index == index) is { } same)
        {
            _list.Select(same);
        }

        ShowDetails(_list.Selected);
    }

    private void ShowDetails(EditorRow? row)
    {
        var pending = Mode.Pending;
        if (pending.Count > 0)
        {
            _details.Show("Unsaved changes (F10 saves)", pending);
        }
        else if (row is not null)
        {
            _details.Show("Details", Mode.Describe(row));
        }
        else
        {
            _details.ShowMessage("Details", Mode.Rows.Count == 0 ? "Nothing here yet. F2 adds an entry." : "Select an entry.");
        }
    }

    private void Act(Func<EditorMode, EditorRow?, CancellationToken, Task<string?>> action, bool needsRow = true)
    {
        var mode = Mode;
        if (mode.ReadOnlyReason(this) is { } reason)
        {
            _details.ShowMessage("Read only", reason);
            return;
        }

        var row = _list.Selected;
        if (needsRow && row is null)
        {
            return;
        }

        RunInBackground(
            ct => action(mode, row, ct),
            error =>
            {
                Refresh();
                if (error is not null)
                {
                    _details.ShowMessage("Can't do that", error);
                }
            },
            "working…");
    }

    private void Save()
    {
        var mode = Mode;
        if (!mode.IsDirty)
        {
            _details.ShowMessage("Save", "Nothing to save.");
            return;
        }

        if (mode.ReadOnlyReason(this) is { } reason)
        {
            _details.ShowMessage("Read only", reason);
            return;
        }

        var scopeNote = mode.UsesScope ? $" ({_scope} scope)" : string.Empty;
        var privileged = mode is HostsMode || (mode.UsesScope && Variables.NeedsPrivileges(_scope));
        if (!AskYesNo("Save " + mode.Title, $"Apply {mode.Pending.Count} change(s){scopeNote}?" + (privileged ? "\n\nThis asks for administrator or root rights for this one write." : string.Empty)))
        {
            return;
        }

        RunInBackground(
            async ct =>
            {
                var result = await mode.SaveAsync(this, ct).ConfigureAwait(false);
                return result;
            },
            result =>
            {
                Refresh();
                _details.ShowMessage(result.Success ? "Saved" : "Not saved", result.Message);
                if (!result.Success && result.ShellCommand is { Length: > 0 } line
                    && AskYesNo("A password is needed", $"Run this in the shell?\n\n{line}"))
                {
                    Complete(new PanelResult(PanelResultKind.RunCommand, line));
                }
            },
            "saving…");
    }
}
