using System.Globalization;
using System.Text.RegularExpressions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.WindowsUpdate;

/// <summary>The fields read from a WUA IUpdate, as plain values (the COM reading lives in <see cref="WuaClient"/>).</summary>
internal sealed record WuaRawUpdate(
    string UpdateId,
    string Title,
    IReadOnlyList<string> KbArticleIds,
    IReadOnlyList<string> Categories,
    decimal? MaxDownloadSize,
    bool IsDownloaded,
    bool IsMandatory,
    bool BrowseOnly,
    int Type,
    string? MsrcSeverity,
    string? Description,
    DateTime? LastDeploymentChangeTime,
    int RebootBehavior);

internal sealed record WuaInstallItem(string UpdateId, string Title, bool Succeeded, string? Error);

/// <summary>Carried in <see cref="ElevatedResponse.Output"/> from the elevated helper back to the service.</summary>
internal sealed record WuaInstallSummary(bool RebootRequired, IReadOnlyList<WuaInstallItem> Items);

/// <summary>Pure helpers for Windows Update Agent data: search criteria, mapping, names and friendly HRESULT messages.</summary>
internal static partial class WuaText
{
    private const int UpdateTypeDriver = 2;

    [GeneratedRegex(@"KB\d{4,8}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KbRegex();

    private static readonly Dictionary<uint, string> Messages = new()
    {
        [0x80240024] = "There are no updates to install.",
        [0x8024402C] = "Windows Update couldn't be reached (host name not resolved). Check your internet connection or proxy.",
        [0x80072EE2] = "The connection to Windows Update timed out.",
        [0x80072EFD] = "Windows Update couldn't be reached. Check your internet connection.",
        [0x80072EFE] = "The connection to Windows Update was interrupted.",
        [0x80072F8F] = "A secure connection to Windows Update failed (check the system clock and TLS settings).",
        [0x8024401C] = "The request to the update server timed out.",
        [0x80244022] = "The update service is temporarily unavailable (HTTP 503). Try again later.",
        [0x8024002E] = "Access to Windows Update is disabled by policy.",
        [0x80070422] = "The Windows Update service is disabled.",
        [0x8024001E] = "The Windows Update service is stopping. Try again in a moment.",
        [0x8024A000] = "The Automatic Updates service is not available.",
        [0x80070005] = "Access denied — administrator rights are required.",
        [0x80240044] = "Only administrators can install updates for all users.",
        [0x80240016] = "Another installation is in progress or a restart is pending.",
        [0x80240017] = "The update is not applicable to this PC.",
        [0x80240020] = "The operation needs a signed-in interactive user.",
        [0x80240022] = "All updates failed to install.",
        [0x8024000B] = "The operation was cancelled.",
        [0x80246007] = "The update has not been downloaded.",
        [0x8024200D] = "The update needs to be downloaded again.",
        [0x80246008] = "Downloading failed (BITS). Check your network connection.",
        [0x800F0922] = "The update failed to install (the system reserved partition may be full or a VPN is interfering).",
        [0x80073712] = "The component store is corrupt. Run 'DISM /Online /Cleanup-Image /RestoreHealth'.",
    };

    public static string BuildCriteria(WindowsUpdateQuery query)
    {
        var parts = new List<string> { "IsInstalled=0" };
        if (!query.IncludeHidden)
        {
            parts.Add("IsHidden=0");
        }

        if (!query.IncludeDrivers)
        {
            parts.Add("Type='Software'");
        }

        if (!query.IncludeOptional)
        {
            parts.Add("BrowseOnly=0");
        }

        return string.Join(" and ", parts);
    }

    public static WindowsUpdateInfo ToInfo(WuaRawUpdate raw) => new(
        raw.UpdateId,
        raw.Title,
        raw.KbArticleIds.Select(k => k.StartsWith("KB", StringComparison.OrdinalIgnoreCase) ? k.ToUpperInvariant() : "KB" + k).FirstOrDefault() ?? ExtractKb(raw.Title),
        raw.Categories,
        raw.MaxDownloadSize is { } size and > 0 ? (long)size : null,
        raw.IsDownloaded,
        raw.IsMandatory,
        raw.Type == UpdateTypeDriver,
        raw.BrowseOnly,
        string.IsNullOrWhiteSpace(raw.MsrcSeverity) ? null : raw.MsrcSeverity,
        string.IsNullOrWhiteSpace(raw.Description) ? null : raw.Description,
        raw.LastDeploymentChangeTime is { } date && date.Year > 1900 ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)) : null,
        raw.RebootBehavior != 0);

    /// <summary>Only updates the query asked for (a second filter in case the criteria string was broader).</summary>
    public static bool Matches(WindowsUpdateInfo update, WindowsUpdateQuery query) =>
        (query.IncludeDrivers || !update.IsDriver) && (query.IncludeOptional || !update.IsOptional);

    public static string? ExtractKb(string? title) => title is null ? null : KbRegex().Match(title) is { Success: true } m ? m.Value.ToUpperInvariant() : null;

    public static string OperationName(int operation) => operation switch
    {
        1 => "Installation",
        2 => "Uninstallation",
        3 => "Other",
        _ => "Unknown",
    };

    public static string ResultName(int resultCode) => resultCode switch
    {
        0 => "Not started",
        1 => "In progress",
        2 => "Succeeded",
        3 => "Succeeded with errors",
        4 => "Failed",
        5 => "Aborted",
        _ => "Unknown",
    };

    public static bool IsSuccess(int resultCode) => resultCode is 2 or 3;

    public static string Describe(int hresult)
    {
        var code = unchecked((uint)hresult);
        var hex = "0x" + code.ToString("X8", CultureInfo.InvariantCulture);
        return Messages.TryGetValue(code, out var message) ? $"{message} ({hex})" : $"Windows Update error {hex}.";
    }
}
