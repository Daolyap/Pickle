using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Commands;

/// <summary><c>pk update …</c> — Windows Update check, install, history and status.</summary>
internal sealed class UpdateCommand : WindowsCommandBase
{
    /// <summary>An online Windows Update scan can take minutes (the first one after a restart especially).</summary>
    internal static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(10);

    public override string Name => "update";

    public override string Description => "Windows Update: check, install, history, status";

    public override string Usage =>
        "pk update check [--drivers] [--optional] [--timeout 10m]\n" +
        "pk update install --all|--kb KB5031455[,KB…]|--id <guid> [--drivers] [--optional] [--yes]\n" +
        "pk update history [--max N]\n" +
        "pk update status";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> rawArgs, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(rawArgs, "kb", "id", "max", "timeout");
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
        var timeout = Busy.ParseTimeout(args.Value("timeout"), SearchTimeout);
        switch (args.Arg(0)?.ToLowerInvariant())
        {
            case "check" or "list" or "search":
                var updates = await SearchAsync(wu, output, query, timeout, cancellationToken).ConfigureAwait(false);
                if (updates.Count == 0)
                {
                    output.Success("No updates available.");
                    return 0;
                }

                output.Heading($"{updates.Count} update(s) available");
                foreach (var update in updates.OrderBy(UpdateGroups.Of).ThenBy(u => u.Title, StringComparer.CurrentCultureIgnoreCase))
                {
                    output.Object(UpdateRow(update));
                }

                if (!query.IncludeDrivers || !query.IncludeOptional)
                {
                    output.Muted($"Not shown: {(query.IncludeDrivers ? string.Empty : "drivers (--drivers) ")}{(query.IncludeOptional ? string.Empty : "optional updates (--optional)")}".TrimEnd());
                }

                return 0;

            case "install":
                return await InstallAsync(wu, output, args, query, timeout, cancellationToken).ConfigureAwait(false);

            case "history":
                var max = args.Value("max") is { } m && int.TryParse(m, out var parsed) && parsed > 0 ? Math.Min(parsed, 1000) : 30;
                var history = await Busy.RunAsync(output, "Reading the update history", (_, ct) => wu.GetHistoryAsync(max, ct), timeout, cancellationToken).ConfigureAwait(false);
                output.Heading($"Last {history.Count} update event(s)");
                foreach (var entry in history)
                {
                    output.Object(Display.Columns(
                        entry,
                        DisplayColumn.Note("Date", CommandOutput.When(entry.Date)),
                        "Result",
                        DisplayColumn.Alias("KB", nameof(WindowsUpdateHistoryEntry.KbArticle)),
                        "Title"));
                }

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

    /// <summary>
    /// Searches with a live status line. The search runs on the Windows Update Agent's own thread; Ctrl+C or the
    /// timeout stop waiting for it (the agent finishes in the background, and the next check joins that search).
    /// </summary>
    internal static Task<IReadOnlyList<WindowsUpdateInfo>> SearchAsync(IWindowsUpdateService wu, CommandOutput output, WindowsUpdateQuery query, TimeSpan timeout, CancellationToken cancellationToken) =>
        Busy.RunAsync(
            output,
            "Searching Windows Update",
            (progress, ct) => wu.SearchAsync(query, new SyncProgress<WindowsUpdateProgress>(p => progress.Report(Describe(p))), ct),
            timeout,
            cancellationToken);

    internal static object UpdateRow(WindowsUpdateInfo update) => Display.Columns(
        update,
        DisplayColumn.Alias("KB", nameof(WindowsUpdateInfo.KbArticle)),
        DisplayColumn.Note("Kind", UpdateGroups.Title(UpdateGroups.Of(update))),
        DisplayColumn.Note("Size", CommandOutput.Size(update.SizeBytes)),
        "Title");

    private static string Describe(WindowsUpdateProgress p) =>
        string.Join(' ', new[] { p.Stage, p.CurrentUpdate, p.Percent is { } pct ? $"{pct:0}%" : null }.Where(x => !string.IsNullOrWhiteSpace(x)));

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
                output.Line($"    {(u.KbArticle ?? string.Empty),-10} {u.Title}  {output.Dim(CommandOutput.Size(u.SizeBytes))}");
            }
        }
    }

    private async Task<int> InstallAsync(IWindowsUpdateService wu, CommandOutput output, CommandArgs args, WindowsUpdateQuery query, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!args.Has("all", "kb", "id"))
        {
            return UsageError(output, "Choose what to install: --all, --kb KB123[,KB456] or --id <guid>.");
        }

        var kbs = new List<string>();
        foreach (var value in (args.Value("kb") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kb = WindowsIds.TryNormalizeKb(value, out var normalized) ? normalized : throw new ArgumentException($"'{value}' is not a KB number.");
            if (!kbs.Contains(kb, StringComparer.OrdinalIgnoreCase))
            {
                kbs.Add(kb);
            }
        }

        string? updateId = null;
        if (args.Value("id") is { } rawId && !WindowsIds.TryNormalizeUpdateId(rawId, out updateId))
        {
            throw new ArgumentException($"'{rawId}' is not a valid update id (GUID).");
        }

        // A KB or an id names one update: look everywhere (drivers, optional/preview updates), not just the default set.
        var specific = kbs.Count > 0 || updateId is not null;
        var search = specific ? query with { IncludeDrivers = true, IncludeOptional = true } : query;
        var available = await SearchAsync(wu, output, search, timeout, cancellationToken).ConfigureAwait(false);
        var selected = available.Where(u =>
            (args.Has("all") && WuaMatches(u, query))
            || (u.KbArticle is { } kb && kbs.Contains(kb, StringComparer.OrdinalIgnoreCase))
            || (updateId is not null && string.Equals(u.UpdateId, updateId, StringComparison.OrdinalIgnoreCase))).ToList();

        var missing = kbs.Where(kb => !available.Any(u => string.Equals(u.KbArticle, kb, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0)
        {
            await ExplainMissingAsync(wu, output, missing, cancellationToken).ConfigureAwait(false);
        }

        if (updateId is not null && !available.Any(u => string.Equals(u.UpdateId, updateId, StringComparison.OrdinalIgnoreCase)))
        {
            output.Warning($"Update {updateId} is not available for this PC.");
        }

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

    private static bool WuaMatches(WindowsUpdateInfo update, WindowsUpdateQuery query) =>
        (query.IncludeDrivers || !update.IsDriver) && (query.IncludeOptional || !update.IsOptional);

    /// <summary>Says why a requested KB isn't offered: already installed (from the history), or not applicable.</summary>
    private static async Task ExplainMissingAsync(IWindowsUpdateService wu, CommandOutput output, IReadOnlyList<string> missing, CancellationToken cancellationToken)
    {
        IReadOnlyList<WindowsUpdateHistoryEntry> history = [];
        try
        {
            history = await wu.GetHistoryAsync(500, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            output.Pickle.Log.Warn("pk", "reading the update history failed", ex);
        }

        foreach (var kb in missing)
        {
            var installed = history
                .Where(h => string.Equals(h.KbArticle, kb, StringComparison.OrdinalIgnoreCase) && h.Operation == "Installation" && h.Result.StartsWith("Succeeded", StringComparison.Ordinal))
                .OrderByDescending(h => h.Date)
                .FirstOrDefault();
            output.Warning(installed is not null
                ? $"{kb} is already installed ({CommandOutput.When(installed.Date)})."
                : $"{kb} is not offered for this PC (already installed, superseded, or not applicable).");
        }
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
        var packages = winget is null ? [] : await Busy.RunAsync(output, "Checking winget for upgrades", (_, ct) => winget.ListUpgradesAsync(includeUnknown, ct), TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        var updates = wu is null ? [] : await UpdateCommand.SearchAsync(wu, output, new WindowsUpdateQuery(IncludeDrivers: args.Has("drivers")), UpdateCommand.SearchTimeout, cancellationToken).ConfigureAwait(false);
        var valid = packages.Where(p => WindowsIds.IsValidWingetId(p.Id)).ToList();
        if (valid.Count == 0 && updates.Count == 0)
        {
            output.Success("Everything is up to date.");
            return 0;
        }

        if (valid.Count > 0)
        {
            output.Heading($"{valid.Count} winget upgrade(s)");
            await WingetCommand.WritePackagesAsync(output, valid).ConfigureAwait(false);
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
