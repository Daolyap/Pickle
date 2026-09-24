using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Commands;

/// <summary><c>pk tool</c>: install command-line tools by command name (the same installs the missing-tool prompt offers).</summary>
internal sealed class ToolCommand : WindowsCommandBase
{
    public override string Name => "tool";

    public override string Description => "Install command-line tools by name (7z, nmap, jq…): for you, all users or this session only";

    public override string Usage =>
        "pk tool install <command|winget-id> [--machine|--temp] [--no-path]\n" +
        "pk tool list [filter]\n" +
        "pk tool temp            (this session's temporary tools)\n" +
        "pk tool remove-temp     (uninstall them now instead of on exit)";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> rawArgs, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(rawArgs);
        if (args.Error is not null)
        {
            return UsageError(output, args.Error);
        }

        var installer = output.Pickle.Services.Get<IToolInstaller>();
        switch (args.Arg(0)?.ToLowerInvariant())
        {
            case "list" or "ls":
                var filter = args.Arg(1);
                foreach (var package in ToolCatalog.All.Where(p => filter is null
                    || p.Command.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                {
                    output.Object(new ToolRow(package.Command, package.Name, package.WingetId));
                }

                return 0;

            case "install" or "add":
                if (installer is not { IsSupported: true })
                {
                    output.Context.WriteError("Installing tools needs winget (Windows).");
                    return 1;
                }

                if (args.Arg(1) is not { Length: > 0 } name)
                {
                    return UsageError(output, "Name a tool, e.g. pk tool install 7z");
                }

                var target = installer.Find(name)
                    ?? (name.Contains('.', StringComparison.Ordinal) ? new ToolPackage(name.Split('.')[^1].ToLowerInvariant(), WindowsIds.RequireWingetId(name), name) : null);
                if (target is null)
                {
                    output.Context.WriteError($"No known package provides '{name}'. Pass a winget id (pk tool install Publisher.App) or search with pk winget search {name}.");
                    return 1;
                }

                var scope = args.Has("temp", "temporary", "t") ? ToolInstallScope.Temporary
                    : args.Has("machine", "m") ? ToolInstallScope.Machine
                    : ToolInstallScope.User;
                var addToPath = args.Has("path") || (!args.Has("no-path") && output.Pickle.Config.Current.Shell.AddInstalledToolsToPath);
                var result = await installer.InstallAsync(target, new ToolInstallOptions(scope, addToPath), new Progress<string>(output.Status), cancellationToken).ConfigureAwait(false);
                if (result.Success)
                {
                    output.Success(result.Message);
                    return 0;
                }

                output.Failure(result.Message);
                return 1;

            case "temp":
                var temporary = installer?.TemporaryInstalls ?? [];
                if (temporary.Count == 0)
                {
                    output.Muted("No temporary tools in this session.");
                }

                foreach (var package in temporary)
                {
                    output.Object(new ToolRow(package.Command, package.Name, package.WingetId));
                }

                return 0;

            case "remove-temp":
                if (installer is null)
                {
                    return 0;
                }

                var removed = await installer.RemoveTemporaryAsync(new Progress<string>(output.Status), cancellationToken).ConfigureAwait(false);
                (removed.Success ? (Action<string>)output.Success : output.Failure)(removed.Message);
                return removed.Success ? 0 : 1;

            case null:
                return UsageError(output);

            case var sub:
                return UsageError(output, $"Unknown subcommand 'pk tool {sub}'.");
        }
    }

    internal sealed record ToolRow(string Command, string Name, string WingetId);
}
