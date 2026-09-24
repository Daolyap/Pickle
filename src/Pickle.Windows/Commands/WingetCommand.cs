using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Commands;

/// <summary><c>pk winget …</c> — list, search, install, upgrade and repair via <see cref="IWingetService"/>.</summary>
internal sealed class WingetCommand : WindowsCommandBase
{
    public override string Name => "winget";

    public override string Description => "Manage winget packages (list, upgrades, search, install, upgrade, uninstall, sources)";

    public override string Usage =>
        "pk winget list|upgrades|search <query>|show <id>|install <id> [--version v] [--scope user|machine]|" +
        "upgrade <id>|--all [--elevated]|uninstall <id>|sources|repair-source [--admin]|install-module [--yes]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> rawArgs, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(rawArgs, "version", "scope");
        if (args.Error is not null)
        {
            return UsageError(output, args.Error);
        }

        var winget = output.Pickle.Services.Get<IWingetService>();
        if (winget is not { IsSupported: true })
        {
            output.Context.WriteError("winget is only available on Windows.");
            return 1;
        }

        var sub = args.Arg(0)?.ToLowerInvariant();
        switch (sub)
        {
            case null or "help" or "status" or "backend":
                var backend = await winget.GetBackendAsync(cancellationToken).ConfigureAwait(false);
                output.Heading("winget");
                output.Line("  backend: " + BackendText(backend));
                if (backend == WingetBackend.Cli)
                {
                    output.Muted("  Tip: 'pk winget install-module' installs Microsoft.WinGet.Client for faster, structured results.");
                }

                output.Muted("usage: " + Usage);
                return 0;

            case "list":
                await MaybeAutoInstallModuleAsync(winget, output, cancellationToken).ConfigureAwait(false);
                var installed = await winget.ListInstalledAsync(cancellationToken).ConfigureAwait(false);
                output.Heading($"{installed.Count} package(s) installed, {installed.Count(p => p.IsUpgradable)} upgradable");
                installed.ToList().ForEach(output.Object);
                return 0;

            case "upgrades":
                var includeUnknown = args.Has("include-unknown") || output.Pickle.Config.Current.Winget.IncludeUnknownVersions;
                var upgrades = await winget.ListUpgradesAsync(includeUnknown, cancellationToken).ConfigureAwait(false);
                output.Heading(upgrades.Count == 0 ? "All packages are up to date." : $"{upgrades.Count} upgrade(s) available");
                upgrades.ToList().ForEach(output.Object);
                return 0;

            case "search":
                var query = args.Rest(1);
                if (query.Length == 0)
                {
                    return UsageError(output, "pk winget search needs a query.");
                }

                var found = await winget.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                output.Heading($"{found.Count} result(s) for '{query}'");
                found.ToList().ForEach(output.Object);
                return 0;

            case "show":
                var showId = WindowsIds.RequireWingetId(args.Arg(1));
                var details = await winget.GetDetailsAsync(showId, cancellationToken).ConfigureAwait(false);
                if (details is null)
                {
                    output.Context.WriteError($"No package found with id '{showId}'.");
                    return 1;
                }

                output.Heading($"{details.Name} [{details.Id}]");
                WriteField(output, "Version", details.LatestVersion);
                WriteField(output, "Publisher", details.Publisher);
                WriteField(output, "Homepage", details.Homepage);
                WriteField(output, "License", details.License);
                WriteField(output, "Description", details.Description);
                output.Object(details);
                return 0;

            case "install":
                return await InstallAsync(winget, output, args, cancellationToken).ConfigureAwait(false);

            case "upgrade":
                return await UpgradeAsync(winget, output, args, cancellationToken).ConfigureAwait(false);

            case "uninstall":
                var removeId = WindowsIds.RequireWingetId(args.Arg(1));
                if (!output.Confirm(args, $"Uninstall {removeId}?", defaultYes: false))
                {
                    output.Muted("Cancelled.");
                    return 1;
                }

                return Report(output, await winget.UninstallAsync(removeId, StageProgress(output), cancellationToken).ConfigureAwait(false));

            case "sources":
                var sources = await winget.ListSourcesAsync(cancellationToken).ConfigureAwait(false);
                output.Heading($"{sources.Count} source(s)");
                sources.ToList().ForEach(output.Object);
                return 0;

            case "repair-source":
                var admin = args.Has("admin", "elevated");
                var question = admin
                    ? "Re-register the winget source package for administrator sessions? This runs Add-AppxPackage from " +
                      "cdn.winget.microsoft.com in an elevated helper (UAC prompt)."
                    : "Re-register the winget source package (Add-AppxPackage from cdn.winget.microsoft.com) for the current user?";
                if (!output.Confirm(args, question, defaultYes: true))
                {
                    output.Muted("Cancelled.");
                    return 1;
                }

                return Report(output, await winget.RepairSourceAsync(admin, cancellationToken).ConfigureAwait(false));

            case "install-module":
                if (!output.Confirm(args, "Install the Microsoft.WinGet.Client module from the PowerShell Gallery for the current user?", defaultYes: true))
                {
                    output.Muted("Cancelled.");
                    return 1;
                }

                return Report(output, await winget.InstallClientModuleAsync(cancellationToken).ConfigureAwait(false));

            default:
                return UsageError(output, $"Unknown subcommand 'pk winget {sub}'.");
        }
    }

    internal static string BackendText(WingetBackend backend) => backend switch
    {
        WingetBackend.PowerShellModule => "Microsoft.WinGet.Client module",
        WingetBackend.Cli => "winget.exe (text output)",
        _ => "unavailable — install App Installer from the Microsoft Store",
    };

    internal static IProgress<WingetProgress> StageProgress(CommandOutput output)
    {
        string? last = null;
        return new SyncProgress<WingetProgress>(p =>
        {
            if (p.Stage == last)
            {
                return;
            }

            last = p.Stage;
            output.Muted($"  {p.Stage}{(p.Message is { Length: > 0 } m && m != p.Stage && p.Stage is not ("Done" or "Failed") ? ": " + m : string.Empty)}");
        });
    }

    internal static int Report(CommandOutput output, WingetOperationResult result)
    {
        if (result.Success)
        {
            output.Success(result.Message);
        }
        else
        {
            output.Failure(result.Message);
        }

        if (result.RebootRequired)
        {
            output.Warning("A restart is required to finish.");
        }

        output.Object(result);
        return result.Success ? 0 : 1;
    }

    private static async Task<int> InstallAsync(IWingetService winget, CommandOutput output, CommandArgs args, CancellationToken cancellationToken)
    {
        var id = WindowsIds.RequireWingetId(args.Arg(1));
        var version = args.Value("version") is { } v ? WindowsIds.RequireWingetVersion(v) : null;
        var scope = args.Value("scope")?.ToLowerInvariant() switch
        {
            null => WingetScope.Any,
            "user" => WingetScope.User,
            "machine" or "system" => WingetScope.Machine,
            var other => throw new ArgumentException($"Unknown scope '{other}' (use user or machine)."),
        };

        var elevatedNote = scope == WingetScope.Machine ? " (machine-wide: administrator rights, UAC prompt)" : string.Empty;
        if (!output.Confirm(args, $"Install {id}{(version is null ? string.Empty : " " + version)}{elevatedNote}?", defaultYes: true))
        {
            output.Muted("Cancelled.");
            return 1;
        }

        var options = new WingetInstallOptions(version, scope, Force: args.Has("force"));
        return Report(output, await winget.InstallAsync(id, options, StageProgress(output), cancellationToken).ConfigureAwait(false));
    }

    private static async Task<int> UpgradeAsync(IWingetService winget, CommandOutput output, CommandArgs args, CancellationToken cancellationToken)
    {
        var elevated = args.Has("elevated", "admin");
        var includeUnknown = args.Has("include-unknown") || output.Pickle.Config.Current.Winget.IncludeUnknownVersions;
        if (!args.Has("all"))
        {
            var id = WindowsIds.RequireWingetId(args.Arg(1));
            if (elevated)
            {
                return await ViaBrokerAsync(output, args, ElevatedOperationKind.WingetUpgrade, [id], cancellationToken).ConfigureAwait(false);
            }

            return Report(output, await winget.UpgradeAsync(id, new WingetInstallOptions(IncludeUnknown: includeUnknown), StageProgress(output), cancellationToken).ConfigureAwait(false));
        }

        var upgrades = await winget.ListUpgradesAsync(includeUnknown, cancellationToken).ConfigureAwait(false);
        if (upgrades.Count == 0)
        {
            output.Success("All packages are up to date.");
            return 0;
        }

        output.Heading($"{upgrades.Count} upgrade(s) available");
        foreach (var p in upgrades)
        {
            output.Line($"  {p.Name}  {output.Dim(p.Id)}  {p.InstalledVersion} → {output.Accent(p.AvailableVersion ?? "?")}");
        }

        if (!output.Confirm(args, $"Upgrade {upgrades.Count} package(s){(elevated ? " with administrator rights (one UAC prompt)" : string.Empty)}?", defaultYes: true))
        {
            output.Muted("Cancelled.");
            return 1;
        }

        var valid = upgrades.Where(p => WindowsIds.IsValidWingetId(p.Id)).ToList();
        foreach (var skipped in upgrades.Except(valid))
        {
            output.Warning($"Skipping '{skipped.Id}': the id is not a plain winget id (truncated or local).");
        }

        if (elevated)
        {
            return await ViaBrokerAsync(output, args, ElevatedOperationKind.WingetUpgrade, [.. valid.Select(p => p.Id)], cancellationToken).ConfigureAwait(false);
        }

        var failures = 0;
        foreach (var package in valid)
        {
            output.Line($"{output.Accent("→")} {package.Name} {output.Dim(package.Id)}");
            var result = await winget.UpgradeAsync(package.Id, new WingetInstallOptions(IncludeUnknown: includeUnknown), StageProgress(output), cancellationToken).ConfigureAwait(false);
            failures += Report(output, result);
        }

        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> ViaBrokerAsync(CommandOutput output, CommandArgs args, ElevatedOperationKind kind, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var broker = output.Pickle.Services.Get<IElevationBroker>();
        if (broker is not { IsSupported: true })
        {
            output.Context.WriteError("Elevation is not available in this session.");
            return 1;
        }

        if (ids.Count == 0)
        {
            output.Muted("Nothing to upgrade.");
            return 0;
        }

        try
        {
            var responses = await broker.RunAsync(
                [new ElevatedRequest(kind, ids)],
                new SyncProgress<string>(m => output.Muted("  " + m)),
                cancellationToken).ConfigureAwait(false);
            var failures = 0;
            foreach (var response in responses)
            {
                failures += Report(output, new WingetOperationResult(response.Success, response.Message, response.ExitCode));
            }

            return failures == 0 ? 0 : 1;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            output.Failure("The administrator (UAC) prompt was declined.");
            return 1;
        }
    }

    private static async Task MaybeAutoInstallModuleAsync(IWingetService winget, CommandOutput output, CancellationToken cancellationToken)
    {
        if (!output.Pickle.Config.Current.Winget.AutoInstallClientModule
            || await winget.GetBackendAsync(cancellationToken).ConfigureAwait(false) != WingetBackend.Cli)
        {
            return;
        }

        output.Muted("Installing Microsoft.WinGet.Client (winget.autoInstallClientModule is on)…");
        var result = await winget.InstallClientModuleAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            output.Warning(result.Message);
        }
    }

    private static void WriteField(CommandOutput output, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            output.Line($"  {output.Dim(name.PadRight(12))}{value.Replace("\n", "\n              ", StringComparison.Ordinal)}");
        }
    }
}
