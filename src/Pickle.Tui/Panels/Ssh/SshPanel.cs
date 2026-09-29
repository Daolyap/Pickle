using Pickle.Abstractions;
using Pickle.Tui.Widgets;
using Pickle.Wizards;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Pickle.Tui.Panels.Ssh;

/// <summary>
/// SSH hosts from ~/.ssh/config and known_hosts. Enter connects here; in Windows Terminal, F2 opens a new tab and F3 a
/// split pane; F4 puts the ssh command in the input line to edit.
/// </summary>
public sealed class SshPanel : PanelWindow
{
    public const string PanelId = "ssh";

    private readonly FilterableList<SshHost> _list;
    private readonly string _sshDirectory;
    private readonly string? _home;

    public SshPanel(PanelContext context)
        : this(context, DefaultSshDirectory(), SegmentsHome(), InWindowsTerminal())
    {
    }

    internal SshPanel(PanelContext context, string sshDirectory, string? home, bool windowsTerminal)
        : base(context, "SSH")
    {
        _sshDirectory = sshDirectory;
        _home = home;
        _list = new FilterableList<SshHost>(h => h.Name)
        {
            Detail = h => h.Target == h.Name ? null : h.Target,
            Category = h => h.Source,
            Keywords = h => h.Target,
            Schemes = Schemes,
        };
        Body.Add(_list);

        _list.ItemAccepted += (_, _) => Finish(PanelResultKind.RunCommand, Command);
        AddHint(Key.Enter, "Connect", () => Finish(PanelResultKind.RunCommand, h => Command(h)));
        if (windowsTerminal)
        {
            AddHint(Key.F2, "New tab", () => Finish(PanelResultKind.RunCommand, h => "wt -w 0 new-tab " + Command(h)));
            AddHint(Key.F3, "Split pane", () => Finish(PanelResultKind.RunCommand, h => "wt -w 0 split-pane " + Command(h)));
        }

        AddHint(Key.F4, "Edit command", () => Finish(PanelResultKind.ReplaceInput, h => Command(h) + " "));
        if (!string.IsNullOrEmpty(context.Argument))
        {
            _list.FilterText = context.Argument;
        }

        _list.Filter.SetFocus();
    }

    internal FilterableList<SshHost> List => _list;

    /// <summary>The PowerShell command line that connects to <paramref name="host"/>.</summary>
    public static string Command(SshHost host) => "ssh " + string.Join(' ', host.Arguments.Select(a => PowerShellQuoting.FormatArgument(a, allowLeadingDash: true)));

    protected override void OnOpened() => RunInBackground(
        _ => Task.FromResult(SshHosts.Load(_sshDirectory, _home)),
        hosts =>
        {
            _list.SetItems(hosts);
            _list.Status = hosts.Count == 0 ? "No hosts in ~/.ssh/config or ~/.ssh/known_hosts" : null;
        },
        "reading ~/.ssh…");

    private static string? SegmentsHome() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home ? home : null;

    private static string DefaultSshDirectory() => Path.Combine(SegmentsHome() ?? ".", ".ssh");

    private static bool InWindowsTerminal() => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"));

    private void Finish(PanelResultKind kind, Func<SshHost, string> text)
    {
        if (_list.Selected is { } host && !IsClosed)
        {
            Complete(new PanelResult(kind, text(host)));
        }
    }
}
