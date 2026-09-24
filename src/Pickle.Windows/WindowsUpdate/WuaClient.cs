using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.WindowsUpdate;

/// <summary>
/// Windows Update Agent COM API via late binding (<c>dynamic</c>). Every call runs on a dedicated STA thread with a
/// timeout because WUA calls can block for minutes (the thread is abandoned, not aborted, on timeout).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WuaClient
{
    public const string ClientApplicationId = "Pickle";

    public static Task<T> RunAsync<T>(Func<T> work, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "pickle-wua",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task.WaitAsync(timeout, cancellationToken);
    }

    public static IReadOnlyList<WuaRawUpdate> Search(string criteria)
    {
        dynamic session = CreateSession();
        dynamic searcher = session.CreateUpdateSearcher();
        dynamic result = searcher.Search(criteria);
        dynamic updates = result.Updates;
        int count = updates.Count;
        var list = new List<WuaRawUpdate>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(Read(updates.Item(i)));
        }

        return list;
    }

    public static IReadOnlyList<WindowsUpdateHistoryEntry> History(int max)
    {
        dynamic session = CreateSession();
        dynamic searcher = session.CreateUpdateSearcher();
        int total = searcher.GetTotalHistoryCount();
        var take = Math.Min(total, Math.Max(0, max));
        var list = new List<WindowsUpdateHistoryEntry>(take);
        if (take == 0)
        {
            return list;
        }

        dynamic entries = searcher.QueryHistory(0, take);
        int count = entries.Count;
        for (var i = 0; i < count; i++)
        {
            dynamic entry = entries.Item(i);
            string title = (string?)entry.Title ?? string.Empty;
            DateTime date = entry.Date;
            int operation = entry.Operation;
            int resultCode = entry.ResultCode;
            list.Add(new WindowsUpdateHistoryEntry(
                title,
                new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)),
                WuaText.OperationName(operation),
                WuaText.ResultName(resultCode),
                WuaText.ExtractKb(title)));
        }

        return list;
    }

    public static (bool RebootRequired, DateTimeOffset? LastSearch, DateTimeOffset? LastInstall) Status()
    {
        var reboot = false;
        DateTimeOffset? lastSearch = null;
        DateTimeOffset? lastInstall = null;
        try
        {
            dynamic info = Create("Microsoft.Update.SystemInfo");
            reboot = info.RebootRequired;
        }
        catch (COMException)
        {
        }

        try
        {
            dynamic auto = Create("Microsoft.Update.AutoUpdate");
            dynamic results = auto.Results;
            lastSearch = AsUtc((object?)results.LastSearchSuccessDate);
            lastInstall = AsUtc((object?)results.LastInstallationSuccessDate);
        }
        catch (COMException)
        {
        }

        return (reboot, lastSearch, lastInstall);
    }

    /// <summary>Downloads (one at a time, for progress) and installs (one batch) the given update ids. Requires elevation.</summary>
    public static WuaInstallSummary Install(IReadOnlyList<string> updateIds, Action<WindowsUpdateProgress> progress, CancellationToken cancellationToken)
    {
        dynamic session = CreateSession();
        dynamic searcher = session.CreateUpdateSearcher();
        progress(new WindowsUpdateProgress("Searching", null, null));
        dynamic found = searcher.Search("IsInstalled=0");
        dynamic updates = found.Updates;
        int count = updates.Count;

        var wanted = new HashSet<string>(updateIds, StringComparer.OrdinalIgnoreCase);
        var selected = new List<(string Id, string Title, dynamic Update)>();
        for (var i = 0; i < count; i++)
        {
            dynamic update = updates.Item(i);
            string id = update.Identity.UpdateID;
            if (wanted.Remove(id))
            {
                selected.Add((id, (string)update.Title, update));
            }
        }

        var items = wanted.Select(id => new WuaInstallItem(id, id, false, "Not found — already installed or no longer applicable.")).ToList();
        dynamic toInstall = Create("Microsoft.Update.UpdateColl");
        var pending = new List<(string Id, string Title)>();
        for (var i = 0; i < selected.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (id, title, update) = selected[i];
            try
            {
                if (!(bool)update.EulaAccepted)
                {
                    update.AcceptEula();
                }

                if (!(bool)update.IsDownloaded)
                {
                    progress(new WindowsUpdateProgress("Downloading", title, Math.Round(50.0 * i / selected.Count, 1)));
                    dynamic single = Create("Microsoft.Update.UpdateColl");
                    single.Add(update);
                    dynamic downloader = session.CreateUpdateDownloader();
                    downloader.Updates = single;
                    dynamic downloaded = downloader.Download();
                    int code = downloaded.ResultCode;
                    if (!WuaText.IsSuccess(code))
                    {
                        int hr = downloaded.HResult;
                        items.Add(new WuaInstallItem(id, title, false, "Download failed: " + WuaText.Describe(hr)));
                        continue;
                    }
                }

                toInstall.Add(update);
                pending.Add((id, title));
            }
            catch (COMException ex)
            {
                items.Add(new WuaInstallItem(id, title, false, WuaText.Describe(ex.HResult)));
            }
        }

        var reboot = false;
        if (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress(new WindowsUpdateProgress("Installing", pending.Count == 1 ? pending[0].Title : $"{pending.Count} updates", 50));
            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = toInstall;
            dynamic result = installer.Install();
            reboot = result.RebootRequired;
            for (var i = 0; i < pending.Count; i++)
            {
                dynamic updateResult = result.GetUpdateResult(i);
                int code = updateResult.ResultCode;
                int hr = updateResult.HResult;
                items.Add(new WuaInstallItem(pending[i].Id, pending[i].Title, WuaText.IsSuccess(code), WuaText.IsSuccess(code) ? null : WuaText.Describe(hr)));
            }
        }

        progress(new WindowsUpdateProgress("Done", null, 100));
        return new WuaInstallSummary(reboot, items);
    }

    private static WuaRawUpdate Read(dynamic update)
    {
        var kbs = new List<string>();
        dynamic kbIds = update.KBArticleIDs;
        int kbCount = kbIds.Count;
        for (var i = 0; i < kbCount; i++)
        {
            kbs.Add((string)kbIds.Item(i));
        }

        var categories = new List<string>();
        dynamic cats = update.Categories;
        int catCount = cats.Count;
        for (var i = 0; i < catCount; i++)
        {
            categories.Add((string)cats.Item(i).Name);
        }

        int reboot = 0;
        try
        {
            reboot = update.InstallationBehavior.RebootBehavior;
        }
        catch (COMException)
        {
        }

        object? deployed = update.LastDeploymentChangeTime;
        object? size = update.MaxDownloadSize;
        return new WuaRawUpdate(
            (string)update.Identity.UpdateID,
            (string?)update.Title ?? string.Empty,
            kbs,
            categories,
            size is null ? null : Convert.ToDecimal(size, System.Globalization.CultureInfo.InvariantCulture),
            (bool)update.IsDownloaded,
            (bool)update.IsMandatory,
            (bool)update.BrowseOnly,
            (int)update.Type,
            (string?)update.MsrcSeverity,
            (string?)update.Description,
            deployed as DateTime?,
            reboot);
    }

    private static dynamic CreateSession()
    {
        dynamic session = Create("Microsoft.Update.Session");
        session.ClientApplicationID = ClientApplicationId;
        return session;
    }

    private static dynamic Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: false)
            ?? throw new InvalidOperationException($"The Windows Update Agent ({progId}) is not available.");
        return Activator.CreateInstance(type) ?? throw new InvalidOperationException($"Could not create {progId}.");
    }

    private static DateTimeOffset? AsUtc(object? value) =>
        value is DateTime date && date.Year > 1900 ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)) : null;
}
