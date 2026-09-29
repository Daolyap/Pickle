using Pickle.Abstractions;

namespace Pickle.Tui.Panels.Ssh;

/// <summary>Registers the SSH hosts panel (Alt+H) and <c>pk ssh</c>.</summary>
public sealed class SshPanelPlugin : IPicklePlugin
{
    public string Id => "pickle.ssh";

    public string DisplayName => "SSH";

    public string Description => "Connect to the hosts in ~/.ssh/config and known_hosts.";

    public void Initialize(IPickleContext context)
    {
        context.Panels.Register(new PanelDescriptor
        {
            Id = SshPanel.PanelId,
            Title = "SSH",
            Description = "SSH hosts from ~/.ssh/config and known_hosts: connect here, in a new tab or a split",
            DefaultKey = "Alt+H",
            CreateView = ctx => new SshPanel(ctx),
        });
        context.Commands.Register(new SshCommand());
    }
}

/// <summary><c>pk ssh [filter]</c>: the panel when interactive, else the host list.</summary>
public sealed class SshCommand : PickleCommandBase
{
    public override string Name => "ssh";

    public override string Description => "Pick an SSH host from ~/.ssh/config and known_hosts and connect";

    public override string Usage => "pk ssh [filter]";

    protected override Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var pickle = output.Pickle;
        var filter = args.Count > 0 ? string.Join(' ', args) : null;
        if (pickle.Services.Get<IPanelHost>() is { } host && pickle.Shell.IsInteractive)
        {
            var result = host.Show(SshPanel.PanelId, filter);
            switch (result?.Kind)
            {
                case PanelResultKind.RunCommand:
                    pickle.Shell.SubmitCommand(result.Text);
                    break;
                case PanelResultKind.ReplaceInput:
                    pickle.Shell.ReplaceInput(result.Text);
                    break;
            }

            return Task.FromResult(0);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var entry in SshHosts.Load(Path.Combine(home, ".ssh"), home)
            .Where(h => filter is null || h.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || h.Target.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            output.Object(new SshHostRow(entry.Name, entry.Target, entry.Source, SshPanel.Command(entry)));
        }

        return Task.FromResult(0);
    }

    private sealed record SshHostRow(string Name, string Target, string Source, string Command);
}
