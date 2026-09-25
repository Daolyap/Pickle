using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.WindowsUpdate;

/// <summary>
/// Windows Update through the Windows Update Agent COM API. Searching and history don't need elevation; installing
/// runs in-process when Pickle is elevated and otherwise goes through the elevation broker (one UAC prompt).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateService : IWindowsUpdateService
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan QuickTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromHours(3);

    private readonly IPickleLogger _log;
    private readonly Func<IElevationBroker?> _broker;
    private readonly IUpdatePolicySource _policy;
    private readonly SharedSearch<IReadOnlyList<WuaRawUpdate>> _searches = new();

    public WindowsUpdateService(IPickleContext context)
        : this(context.Log, () => context.Services.Get<IElevationBroker>(), new RegistryPolicySource())
    {
    }

    internal WindowsUpdateService(IPickleLogger log, Func<IElevationBroker?> broker, IUpdatePolicySource policy)
    {
        _log = log;
        _broker = broker;
        _policy = policy;
    }

    public bool IsSupported => true;

    public async Task<WindowsUpdateStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var (managed, reason) = UpdatePolicyEvaluator.Evaluate(_policy);
        var (reboot, lastSearch, lastInstall) = await Wrap(() => WuaClient.RunAsync(WuaClient.Status, QuickTimeout, cancellationToken)).ConfigureAwait(false);
        return new WindowsUpdateStatus(true, managed, reason, reboot, lastSearch, lastInstall);
    }

    public Task<IReadOnlyList<WindowsUpdateInfo>> SearchAsync(WindowsUpdateQuery query, CancellationToken cancellationToken = default) =>
        SearchAsync(query, null, cancellationToken);

    public async Task<IReadOnlyList<WindowsUpdateInfo>> SearchAsync(WindowsUpdateQuery query, IProgress<WindowsUpdateProgress>? progress, CancellationToken cancellationToken = default)
    {
        var criteria = WuaText.BuildCriteria(query);
        _log.Info("wua", "search: " + criteria);
        var raw = await Wrap(() => _searches.RunAsync(
            criteria,
            report => WuaClient.RunAsync(() => WuaClient.Search(criteria, report), Timeout.InfiniteTimeSpan, CancellationToken.None),
            progress,
            SearchTimeout,
            cancellationToken)).ConfigureAwait(false);
        var updates = raw.Select(WuaText.ToInfo).Where(u => WuaText.Matches(u, query)).ToList();
        _log.Info("wua", $"search found {updates.Count} update(s)");
        return updates;
    }

    public async Task<WindowsUpdateInstallResult> InstallAsync(IReadOnlyList<string> updateIds, IProgress<WindowsUpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        foreach (var value in updateIds)
        {
            if (!WindowsIds.TryNormalizeUpdateId(value, out var id))
            {
                throw new ArgumentException($"'{value}' is not a valid update id (GUID).");
            }

            ids.Add(id);
        }

        if (ids.Count == 0)
        {
            return new WindowsUpdateInstallResult(true, false, [], "Nothing to install.");
        }

        var broker = _broker();
        if (Environment.IsPrivilegedProcess || broker is null or { IsElevated: true })
        {
            _log.Info("wua", $"installing {ids.Count} update(s) in-process");
            var summary = await Wrap(() => WuaClient.RunAsync(
                () => WuaClient.Install(ids, p => progress?.Report(p), cancellationToken),
                InstallTimeout,
                cancellationToken)).ConfigureAwait(false);
            return ToResult(summary);
        }

        _log.Info("wua", $"installing {ids.Count} update(s) via the elevation broker");
        progress?.Report(new WindowsUpdateProgress("Elevating", "Waiting for the administrator (UAC) prompt…", null));
        try
        {
            var responses = await broker.RunAsync(
                [new ElevatedRequest(ElevatedOperationKind.WindowsUpdateInstall, ids)],
                new SyncProgress<string>(m => progress?.Report(new WindowsUpdateProgress("Elevated", m, null))),
                cancellationToken).ConfigureAwait(false);
            var response = responses.FirstOrDefault() ?? new ElevatedResponse(false, "The elevated helper returned no result.", 1);
            if (ParseSummary(response.Output) is { } summary)
            {
                return ToResult(summary);
            }

            return new WindowsUpdateInstallResult(response.Success, false, [], response.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WindowsUpdateInstallResult(false, false, [], "The administrator (UAC) prompt was declined; nothing was installed.");
        }
    }

    public Task<IReadOnlyList<WindowsUpdateHistoryEntry>> GetHistoryAsync(int max = 50, CancellationToken cancellationToken = default) =>
        Wrap(() => WuaClient.RunAsync(() => WuaClient.History(max), QuickTimeout, cancellationToken));

    internal static WindowsUpdateInstallResult ToResult(WuaInstallSummary summary)
    {
        var results = summary.Items.Select(i => (i.UpdateId, i.Title, i.Succeeded, i.Error)).ToList();
        var ok = results.Count(r => r.Succeeded);
        var message = results.Count == 0
            ? "Nothing was installed."
            : $"{ok} of {results.Count} update(s) installed" + (summary.RebootRequired ? "; restart required to finish." : ".");
        return new WindowsUpdateInstallResult(results.Count > 0 && ok == results.Count, summary.RebootRequired, results, message);
    }

    internal static WuaInstallSummary? ParseSummary(string? output)
    {
        if (string.IsNullOrEmpty(output) || output.Length > 60_000)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WuaInstallSummary>(output, PickleJson.Compact);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<T> Wrap<T>(Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (COMException ex)
        {
            _log.Warn("wua", "Windows Update call failed", ex);
            throw new InvalidOperationException(WuaText.Describe(ex.HResult), ex);
        }
        catch (TimeoutException ex)
        {
            _log.Warn("wua", "Windows Update call timed out", ex);
            throw new InvalidOperationException("Windows Update did not respond in time. Try again later.", ex);
        }
    }
}
