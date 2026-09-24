using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Commands;

/// <summary><c>pk winget …</c> — list, search, install, upgrade, uninstall and repair via <see cref="IWingetService"/>.</summary>
internal sealed class WingetCommand : WindowsCommandBase
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RepairTimeout = TimeSpan.FromMinutes(15);

    public override string Name => "winget";

    public override string Description => "Manage winget packages (list, upgrades, search, install, upgrade, uninstall, sources)";

    public override string Usage =>
        "pk winget list|upgrades|search <query>|show <id>|sources  [--timeout 5m]\n" +
        "pk winget install <id> [--version v] [--scope user|machine]\n" +
        "pk winget upgrade <id>|--all [--elevated]\n" +
        "pk winget uninstall <id> [<id>…] [--elevated] [--yes]\n" +
        "pk winget repair-source [--admin] [--verbose]\n" +
        "pk winget install-module [--yes]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> rawArgs, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(rawArgs, "version", "scope", "timeout");
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

        var timeout = Busy.ParseTimeout(args.Value("timeout"), QueryTimeout);
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

                output.Muted("usage: " + Usage.Replace("\n", "\n       ", StringComparison.Ordinal));
                return 0;

            case "list":
                await MaybeAutoInstallModuleAsync(winget, output, cancellationToken).ConfigureAwait(false);
                var installed = await Busy.RunAsync(output, "Listing installed packages", (_, ct) => winget.ListInstalledAsync(ct), timeout, cancellationToken).ConfigureAwait(false);
                output.Heading($"{installed.Count} package(s) installed, {installed.Count(p => p.IsUpgradable)} upgradable");
                foreach (var package in installed)
                {
                    output.Object(InstalledRow(package));
                }

                return 0;

            case "upgrades":
                var includeUnknown = args.Has("include-unknown") || output.Pickle.Config.Current.Winget.IncludeUnknownVersions;
                var upgrades = await Busy.RunAsync(output, "Checking for upgrades", (_, ct) => winget.ListUpgradesAsync(includeUnknown, ct), timeout, cancellationToken).ConfigureAwait(false);
                output.Heading(upgrades.Count == 0 ? "All packages are up to date." : $"{upgrades.Count} upgrade(s) available");
                foreach (var package in upgrades)
                {
                    output.Object(InstalledRow(package));
                }

                return 0;

            case "search":
                var query = args.Rest(1);
                if (query.Length == 0)
                {
                    return UsageError(output, "pk winget search needs a query.");
                }

                var found = await Busy.RunAsync(output, $"Searching winget for '{query}'", (_, ct) => winget.SearchAsync(query, ct), timeout, cancellationToken).ConfigureAwait(false);
                output.Heading($"{found.Count} result(s) for '{query}'");
                foreach (var package in found)
                {
                    output.Object(Display.Columns(package, "Name", "Id", DisplayColumn.Alias("Version", nameof(WingetPackage.AvailableVersion)), "Source"));
                }

                return 0;

            case "show":
                var showId = WindowsIds.RequireWingetId(args.Arg(1));
                var details = await Busy.RunAsync(output, $"Looking up {showId}", (_, ct) => winget.GetDetailsAsync(showId, ct), timeout, cancellationToken).ConfigureAwait(false);
                if (details is null)
                {
                    output.Context.WriteError($"No package found with id '{showId}'.");
                    return 1;
                }

                output.Heading($"{details.Name} [{details.Id}]");
                output.Object(Display.Columns(
                    details,
                    DisplayColumn.Alias("Version", nameof(WingetPackageDetails.LatestVersion)),
                    "Publisher",
                    "Homepage",
                    "License",
                    DisplayColumn.Note("Versions", details.AvailableVersions.Count == 0 ? null : string.Join(", ", details.AvailableVersions.Take(8)) + (details.AvailableVersions.Count > 8 ? ", …" : string.Empty)),
                    "Description"));
                return 0;

            case "install":
                return await InstallAsync(winget, output, args, cancellationToken).ConfigureAwait(false);

            case "upgrade":
                return await UpgradeAsync(winget, output, args, cancellationToken).ConfigureAwait(false);

            case "uninstall" or "remove":
                return await UninstallAsync(winget, output, args, cancellationToken).ConfigureAwait(false);

            case "sources":
                var sources = await Busy.RunAsync(output, "Listing sources", (_, ct) => winget.ListSourcesAsync(ct), timeout, cancellationToken).ConfigureAwait(false);
                output.Heading($"{sources.Count} source(s)");
                sources.ToList().ForEach(output.Object);
                return 0;

            case "repair-source":
                var admin = args.Has("admin", "elevated");
                var question = admin
                    ? "Re-register the winget source package for the administrator account? This runs Add-AppxPackage from " +
                      "cdn.winget.microsoft.com in an elevated helper (UAC prompt). Only needed when elevated shells run as another account."
                    : "Re-register the winget source package (Add-AppxPackage from cdn.winget.microsoft.com) for the current user?";
                if (!output.Confirm(args, question, defaultYes: true))
                {
                    output.Muted("Cancelled.");
                    return 1;
                }

                var repair = await Busy.RunAsync(
                    output,
                    admin ? "Repairing the winget source (elevated)" : "Repairing the winget source",
                    (_, ct) => winget.RepairSourceAsync(admin, ct),
                    Busy.ParseTimeout(args.Value("timeout"), RepairTimeout),
                    cancellationToken).ConfigureAwait(false);
                return Report(output, repair, verbose: args.Has("verbose", "v"));

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

    internal static object InstalledRow(WingetPackage package) => Display.Columns(
        package,
        "Name",
        "Id",
        DisplayColumn.Alias("Version", nameof(WingetPackage.InstalledVersion)),
        DisplayColumn.Alias("Available", nameof(WingetPackage.AvailableVersion)));

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

    /// <summary>Prints the outcome (and, on failure or with <paramref name="verbose"/>, the program output). Returns the exit code.</summary>
    internal static int Report(CommandOutput output, WingetOperationResult result, bool verbose = false)
    {
        if (result.Success)
        {
            output.Success(result.Message);
        }
        else
        {
            output.Failure(result.Message);
        }

        if (!result.Success || verbose)
        {
            output.Transcript(result.Output);
        }

        if (result.RebootRequired)
        {
            output.Warning("A restart is required to finish.");
        }

        return result.Success ? 0 : 1;
    }

    /// <summary>Aligned "name  id  installed → available" lines that fit <paramref name="width"/> columns.</summary>
    internal static IReadOnlyList<string> PackageLines(IReadOnlyList<WingetPackage> packages, int width, Func<string, string>? dim = null, Func<string, string>? accent = null)
    {
        dim ??= s => s;
        accent ??= s => s;
        if (packages.Count == 0)
        {
            return [];
        }

        const int Indent = 2;
        var versionWidth = Math.Min(16, packages.Max(p => TextWidth.VisibleWidth(p.InstalledVersion ?? "?")));
        var availableWidth = Math.Min(16, packages.Max(p => TextWidth.VisibleWidth(p.AvailableVersion ?? "?")));
        var nameWidth = packages.Max(p => TextWidth.VisibleWidth(p.Name));
        var idWidth = packages.Max(p => TextWidth.VisibleWidth(p.Id));
        var fixedWidth = Indent + 2 + 2 + versionWidth + 3 + availableWidth;
        var room = Math.Max(24, width - 1 - fixedWidth);
        if (nameWidth + idWidth > room)
        {
            // Shrink the longer column first; neither goes below 12.
            var half = room / 2;
            (nameWidth, idWidth) = nameWidth <= half ? (nameWidth, room - nameWidth)
                : idWidth <= half ? (room - idWidth, idWidth)
                : (half, room - half);
            nameWidth = Math.Max(12, nameWidth);
            idWidth = Math.Max(12, idWidth);
        }

        return [.. packages.Select(p =>
            new string(' ', Indent)
            + TextWidth.PadRight(TextWidth.Truncate(p.Name, nameWidth), nameWidth) + "  "
            + dim(TextWidth.PadRight(TextWidth.Truncate(p.Id, idWidth), idWidth)) + "  "
            + TextWidth.PadRight(TextWidth.Truncate(p.InstalledVersion ?? "?", versionWidth), versionWidth) + " → "
            + accent(TextWidth.Truncate(p.AvailableVersion ?? "?", availableWidth)))];
    }

    internal static async Task WritePackagesAsync(CommandOutput output, IReadOnlyList<WingetPackage> packages)
    {
        var width = await output.WidthAsync().ConfigureAwait(false);
        foreach (var line in PackageLines(packages, width, output.Dim, output.Accent))
        {
            output.Line(line);
        }
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
        return Report(output, await winget.InstallAsync(id, options, StageProgress(output), cancellationToken).ConfigureAwait(false), args.Has("verbose", "v"));
    }

    private static async Task<int> UninstallAsync(IWingetService winget, CommandOutput output, CommandArgs args, CancellationToken cancellationToken)
    {
        var ids = args.Positional.Skip(1).Select(WindowsIds.RequireWingetId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0)
        {
            throw new ArgumentException("pk winget uninstall needs at least one package id.");
        }

        var elevated = args.Has("elevated", "admin");
        var what = ids.Count == 1 ? ids[0] : $"{ids.Count} packages ({string.Join(", ", ids)})";
        var uac = elevated ? " with administrator rights (one UAC prompt)" : string.Empty;
        if (!output.Confirm(args, $"Uninstall {what}{uac}?", defaultYes: false))
        {
            output.Muted("Cancelled.");
            return 1;
        }

        if (elevated)
        {
            return Report(output, await winget.UninstallElevatedAsync(ids, StageProgress(output), cancellationToken).ConfigureAwait(false), args.Has("verbose", "v"));
        }

        var failures = 0;
        foreach (var id in ids)
        {
            if (ids.Count > 1)
            {
                output.Line($"{output.Accent("→")} {id}");
            }

            failures += Report(output, await winget.UninstallAsync(id, StageProgress(output), cancellationToken).ConfigureAwait(false), args.Has("verbose", "v"));
        }

        if (failures > 0)
        {
            output.Muted("Machine-wide packages may need administrator rights: add --elevated.");
        }

        return failures == 0 ? 0 : 1;
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
                return await ViaBrokerAsync(output, ElevatedOperationKind.WingetUpgrade, [id], cancellationToken).ConfigureAwait(false);
            }

            return Report(output, await winget.UpgradeAsync(id, new WingetInstallOptions(IncludeUnknown: includeUnknown), StageProgress(output), cancellationToken).ConfigureAwait(false));
        }

        var upgrades = await Busy.RunAsync(output, "Checking for upgrades", (_, ct) => winget.ListUpgradesAsync(includeUnknown, ct), QueryTimeout, cancellationToken).ConfigureAwait(false);
        if (upgrades.Count == 0)
        {
            output.Success("All packages are up to date.");
            return 0;
        }

        output.Heading($"{upgrades.Count} upgrade(s) available");
        await WritePackagesAsync(output, upgrades).ConfigureAwait(false);

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
            return await ViaBrokerAsync(output, ElevatedOperationKind.WingetUpgrade, [.. valid.Select(p => p.Id)], cancellationToken).ConfigureAwait(false);
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

    private static async Task<int> ViaBrokerAsync(CommandOutput output, ElevatedOperationKind kind, IReadOnlyList<string> ids, CancellationToken cancellationToken)
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
                failures += Report(output, new WingetOperationResult(response.Success, response.Message, response.ExitCode) { Output = response.Output });
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
}
