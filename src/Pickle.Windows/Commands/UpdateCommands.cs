using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Commands;

/// <summary><c>pk update …</c> — Windows Update check, install, history and status.</summary>
internal sealed class UpdateCommand : WindowsCommandBase
{
    public override string Name => "update";

    public override string Description => "Windows Update: check, install, history, status";

    public override string Usage => "pk update check|install [--all|--kb KB123|--id <guid>] [--drivers] [--optional] [--yes]|history [--max N]|status";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> rawArgs, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(rawArgs, "kb", "id", "max");
        if (args.Error is not null)
        {
            return UsageError(output, args.Error);
        }

        var wu = output.Pickle.Services.Get<IWindowsUpdateService>();
        if (wu is not { IsSupported: true })
        {
            output.Context.WriteError("Windows Update is only available on Windows.");
            return 1;
        }

        var query = new WindowsUpdateQuery(IncludeDrivers: args.Has("drivers"), IncludeOptional: args.Has("optional"));
        switch (args.Arg(0)?.ToLowerInvariant())
        {
            case "check" or "list" or "search":
                output.Muted("Searching for updates…");
                var updates = await wu.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                WriteUpdateList(output, updates);
                updates.ToList().ForEach(output.Object);
                return 0;

            case "install":
                return await InstallAsync(wu, output, args, query, cancellationToken).ConfigureAwait(false);

            case "history":
                var max = args.Value("max") is { } m && int.TryParse(m, out var parsed) && parsed > 0 ? Math.Min(parsed, 1000) : 30;
                var history = await wu.GetHistoryAsync(max, cancellationToken).ConfigureAwait(false);
                output.Heading($"Last {history.Count} update event(s)");
                history.ToList().ForEach(output.Object);
                return 0;

            case "status":
                var status = await wu.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                WriteStatus(output, status);
                output.Object(status);
                return 0;

            case null:
                return UsageError(output);

            case var sub:
                return UsageError(output, $"Unknown subcommand 'pk update {sub}'.");
        }
    }

    internal static void WriteStatus(CommandOutput output, WindowsUpdateStatus status)
    {
        output.Heading("Windows Update");
        output.Line("  managed by organization: " + (status.IsManagedByOrganization ? output.Warn("yes") + " — " + status.ManagedReason : "no"));
        output.Line("  restart required:        " + (status.RebootRequired ? output.Warn("yes") : "no"));
        output.Line("  last check:              " + CommandOutput.When(status.LastSearchSuccess));
        output.Line("  last install:            " + CommandOutput.When(status.LastInstallSuccess));
    }

    internal static void WriteUpdateList(CommandOutput output, IReadOnlyList<WindowsUpdateInfo> updates)
    {
        if (updates.Count == 0)
        {
            output.Success("No updates available.");
            return;
        }

        output.Heading($"{updates.Count} update(s) available");
        foreach (var group in updates.GroupBy(UpdateGroups.Of).OrderBy(g => g.Key))
        {
            output.Line(output.Accent("  " + UpdateGroups.Title(group.Key)));
            foreach (var u in group)
            {
                output.Line($"    {(u.KbArticle ?? string.Empty).PadRight(10)} {u.Title} {output.Dim(CommandOutput.Size(u.SizeBytes))}");
            }
        }
    }

    private async Task<int> InstallAsync(IWindowsUpdateService wu, CommandOutput output, CommandArgs args, WindowsUpdateQuery query, CancellationToken cancellationToken)
    {
        if (!args.Has("all", "kb", "id"))
        {
            return UsageError(output, "Choose what to install: --all, --kb KB123[,KB456] or --id <guid>.");
        }

        var kbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in (args.Value("kb") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            kbs.Add(WindowsIds.TryNormalizeKb(value, out var kb) ? kb : throw new ArgumentException($"'{value}' is not a KB number."));
        }

        string? updateId = null;
        if (args.Value("id") is { } rawId && !WindowsIds.TryNormalizeUpdateId(rawId, out updateId))
        {
            throw new ArgumentException($"'{rawId}' is not a valid update id (GUID).");
        }

        output.Muted("Searching for updates…");
        var available = await wu.SearchAsync(query with { IncludeDrivers = query.IncludeDrivers || updateId is not null, IncludeOptional = query.IncludeOptional || updateId is not null }, cancellationToken).ConfigureAwait(false);
        var selected = available.Where(u =>
            args.Has("all")
            || (u.KbArticle is { } kb && kbs.Contains(kb))
            || (updateId is not null && string.Equals(u.UpdateId, updateId, StringComparison.OrdinalIgnoreCase))).ToList();
        if (selected.Count == 0)
        {
            output.Context.WriteError("No matching updates are available.");
            return 1;
        }

        WriteUpdateList(output, selected);
        var broker = output.Pickle.Services.Get<IElevationBroker>();
        var uac = broker is { IsElevated: false } ? " This needs administrator rights (UAC prompt)." : string.Empty;
        if (!output.Confirm(args, $"Install {selected.Count} update(s)?{uac}", defaultYes: true))
        {
            output.Muted("Cancelled.");
            return 1;
        }

        return Report(output, await wu.InstallAsync([.. selected.Select(u => u.UpdateId)], StageProgress(output), cancellationToken).ConfigureAwait(false));
    }

    internal static IProgress<WindowsUpdateProgress> StageProgress(CommandOutput output)
    {
        string? last = null;
        return new SyncProgress<WindowsUpdateProgress>(p =>
        {
            var line = $"  {p.Stage} {p.CurrentUpdate}".TrimEnd();
            if (line != last)
            {
                last = line;
                output.Muted(line);
            }
        });
    }

    internal static int Report(CommandOutput output, WindowsUpdateInstallResult result)
    {
        foreach (var (_, title, succeeded, error) in result.Results)
        {
            if (succeeded)
            {
                output.Success(title);
            }
            else
            {
                output.Failure($"{title}: {error}");
            }
        }

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
            output.Warning("Restart your PC to finish installing updates.");
        }

        output.Object(result);
        return result.Success ? 0 : 1;
    }
}

/// <summary>Grouping used by <c>pk update</c> and the updates panel: security/critical first, then drivers, then optional.</summary>
internal static class UpdateGroups
{
    public const int SecurityCritical = 0;
    public const int Other = 1;
    public const int Drivers = 2;
    public const int Optional = 3;

    public static int Of(WindowsUpdateInfo update)
    {
        if (update.IsOptional)
        {
            return Optional;
        }

        if (update.IsDriver)
        {
            return Drivers;
        }

        var security = update.Severity is not null
            || update.Categories.Any(c => c.Contains("Security", StringComparison.OrdinalIgnoreCase) || c.Contains("Critical", StringComparison.OrdinalIgnoreCase));
        return security ? SecurityCritical : Other;
    }

    public static string Title(int group) => group switch
    {
        SecurityCritical => "Security & critical",
        Drivers => "Drivers",
        Optional => "Optional",
        _ => "Other updates",
    };
}

/// <summary><c>pk upgrade</c> — every winget upgrade plus (optionally) Windows updates, one summary and one confirmation.</summary>
internal sealed class UpgradeCommand : WindowsCommandBase
{
    public override string Name => "upgrade";

    public override string Description => "Upgrade all winget packages and install Windows updates";

    public override string Usage => "pk upgrade [--no-windows-update] [--drivers] [--include-unknown] [--yes]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> rawArgs, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(rawArgs);
        var services = output.Pickle.Services;
        var config = output.Pickle.Config.Current.Winget;
        var winget = services.Get<IWingetService>() is { IsSupported: true } w ? w : null;
        var wu = services.Get<IWindowsUpdateService>() is { IsSupported: true } u && config.IncludeWindowsUpdatesInUpgrade && !args.Has("no-windows-update") ? u : null;
        if (winget is null && wu is null)
        {
            output.Context.WriteError("Nothing to upgrade: winget and Windows Update are only available on Windows.");
            return 1;
        }

        var includeUnknown = args.Has("include-unknown") || config.IncludeUnknownVersions;
        output.Muted("Checking for upgrades…");
        var packages = winget is null ? [] : await winget.ListUpgradesAsync(includeUnknown, cancellationToken).ConfigureAwait(false);
        var updates = wu is null ? [] : await wu.SearchAsync(new WindowsUpdateQuery(IncludeDrivers: args.Has("drivers")), cancellationToken).ConfigureAwait(false);
        var valid = packages.Where(p => WindowsIds.IsValidWingetId(p.Id)).ToList();
        if (valid.Count == 0 && updates.Count == 0)
        {
            output.Success("Everything is up to date.");
            return 0;
        }

        if (valid.Count > 0)
        {
            output.Heading($"{valid.Count} winget upgrade(s)");
            foreach (var p in valid)
            {
                output.Line($"  {p.Name}  {output.Dim(p.Id)}  {p.InstalledVersion} → {output.Accent(p.AvailableVersion ?? "?")}");
            }
        }

        if (updates.Count > 0)
        {
            UpdateCommand.WriteUpdateList(output, updates);
        }

        var broker = services.Get<IElevationBroker>();
        var uac = updates.Count > 0 && broker is { IsElevated: false } ? " Windows updates need administrator rights (one UAC prompt)." : string.Empty;
        if (!output.Confirm(args, $"Install {valid.Count} package upgrade(s) and {updates.Count} Windows update(s)?{uac}", defaultYes: true))
        {
            output.Muted("Cancelled.");
            return 1;
        }

        var failures = 0;
        foreach (var package in valid)
        {
            output.Line($"{output.Accent("→")} {package.Name} {output.Dim(package.Id)}");
            var result = await winget!.UpgradeAsync(package.Id, new WingetInstallOptions(IncludeUnknown: includeUnknown), WingetCommand.StageProgress(output), cancellationToken).ConfigureAwait(false);
            failures += WingetCommand.Report(output, result);
        }

        if (updates.Count > 0)
        {
            output.Line($"{output.Accent("→")} Windows Update");
            failures += UpdateCommand.Report(output, await wu!.InstallAsync([.. updates.Select(x => x.UpdateId)], UpdateCommand.StageProgress(output), cancellationToken).ConfigureAwait(false));
        }

        return failures == 0 ? 0 : 1;
    }
}
