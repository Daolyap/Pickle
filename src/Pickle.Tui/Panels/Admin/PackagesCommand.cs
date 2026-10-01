using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Tui.Panels.Admin;

/// <summary><c>pk pkg</c>: the system's package manager (apt, dnf, pacman, zypper, Homebrew) as a panel or scriptable commands.</summary>
internal sealed class PackagesCommand : PanelCommand
{
    private static readonly Dictionary<string, PackageAction> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["install"] = PackageAction.Install,
        ["remove"] = PackageAction.Remove,
        ["upgrade"] = PackageAction.Upgrade,
        ["upgrade-all"] = PackageAction.UpgradeAll,
        ["update"] = PackageAction.RefreshIndex,
    };

    public override string Name => "pkg";

    public override string Description => "System packages: apt, dnf, pacman, zypper or Homebrew";

    public override string Usage => "pk pkg [search text] | list [filter] | upgrades | info <name> | install|remove|upgrade <name>… | upgrade-all | update [--yes]";

    public override IReadOnlyList<string> Examples =>
    [
        "pk pkg                  open the panel (Alt+K)",
        "pk pkg upgrades         what has an update",
        "pk pkg install ripgrep  runs with sudo (asks for the password in the shell when needed)",
    ];

    protected override string PanelId => PackagesPanel.PanelId;

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        if (raw.Count > 0 && Verbs.TryGetValue(raw[0], out var action))
        {
            var args = CommandArgs.Parse(raw.Skip(1).ToList());
            var manager = Manager(output);
            var names = args.Positional;
            var line = manager.ShellCommand(action, names);
            if (!output.Confirm(args, $"Run: {line}", true))
            {
                output.Muted("Cancelled.");
                return 1;
            }

            var result = await manager.RunAsync(action, names, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                output.Success(result.Message);
                return 0;
            }

            output.Failure(result.Message);
            if (result.ShellCommand is { } command)
            {
                output.Muted("It needs a password. Run: " + command);
            }

            return 1;
        }

        if (raw.Count > 0 && raw[0].Equals("search", StringComparison.OrdinalIgnoreCase) && raw.Count > 1 && !output.Pickle.Shell.IsInteractive)
        {
            return await ListAsync(output, raw, cancellationToken).ConfigureAwait(false);
        }

        return await base.RunAsync(output, raw, cancellationToken).ConfigureAwait(false);
    }

    protected override string? PanelArgument(IReadOnlyList<string> args) =>
        args.Count > 1 && args[0].Equals("search", StringComparison.OrdinalIgnoreCase) ? string.Join(' ', args.Skip(1)) : null;

    protected override async Task<int> ListAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var manager = Manager(output);
        var args = CommandArgs.Parse(raw.Count > 0 && raw[0] is "list" or "search" or "upgrades" or "info" ? raw : ["list", .. raw]);
        var verb = args.Arg(0)!.ToLowerInvariant();
        var rest = string.Join(' ', args.Positional.Skip(1));
        switch (verb)
        {
            case "upgrades":
                foreach (var package in await manager.ListUpgradesAsync(cancellationToken).ConfigureAwait(false))
                {
                    output.Object(Display.Columns(package, "Name", "Version", "NewVersion", "Repository"));
                }

                return 0;
            case "search":
                if (rest.Length == 0)
                {
                    return UsageError(output, "Give something to search for.");
                }

                foreach (var package in await manager.SearchAsync(rest, cancellationToken).ConfigureAwait(false))
                {
                    output.Object(Display.Columns(package, "Name", "Installed", "Description"));
                }

                return 0;
            case "info":
                if (rest.Length == 0)
                {
                    return UsageError(output, "Name a package.");
                }

                foreach (var line in await manager.InfoAsync(rest, cancellationToken).ConfigureAwait(false))
                {
                    output.Line(line);
                }

                return 0;
            default:
                foreach (var package in (await manager.ListInstalledAsync(cancellationToken).ConfigureAwait(false))
                    .Where(p => rest.Length == 0 || p.Name.Contains(rest, StringComparison.OrdinalIgnoreCase) || (p.Description?.Contains(rest, StringComparison.OrdinalIgnoreCase) ?? false)))
                {
                    output.Object(Display.Columns(package, "Name", "Version", "Description"));
                }

                return 0;
        }
    }

    private static ISystemPackageManager Manager(CommandOutput output) =>
        output.Pickle.Services.Get<ISystemPackageManager>() is { IsSupported: true } manager
            ? manager
            : throw new InvalidOperationException("No supported package manager (apt, dnf, pacman, zypper or Homebrew) was found. On Windows use 'pk winget'.");
}
