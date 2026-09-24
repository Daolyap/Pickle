using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Pickle.Windows.WindowsUpdate;

/// <summary>Reads HKLM policy values (abstracted so the evaluation logic is tested without a registry).</summary>
internal interface IUpdatePolicySource
{
    /// <summary>Value under <c>HKLM\{subKey}</c>, or null when the key or value doesn't exist.</summary>
    object? GetValue(string subKey, string name);
}

/// <summary>Decides whether Windows Update is managed by an organization (WSUS, WUfB, Group Policy).</summary>
internal static class UpdatePolicyEvaluator
{
    public const string WindowsUpdateKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    public const string AutoUpdateKey = WindowsUpdateKey + @"\AU";

    private static readonly string[] WufbValues =
    [
        "DeferFeatureUpdates", "DeferQualityUpdates", "DeferFeatureUpdatesPeriodInDays", "DeferQualityUpdatesPeriodInDays",
        "TargetReleaseVersion", "PauseFeatureUpdatesStartTime", "PauseQualityUpdatesStartTime", "BranchReadinessLevel",
    ];

    public static (bool Managed, string? Reason) Evaluate(IUpdatePolicySource source)
    {
        var reasons = new List<string>();

        if (AsInt(source.GetValue(AutoUpdateKey, "UseWUServer")) == 1)
        {
            var server = source.GetValue(WindowsUpdateKey, "WUServer") as string;
            reasons.Add(string.IsNullOrWhiteSpace(server)
                ? "Updates come from your organization's update server (WSUS)"
                : $"Updates come from your organization's update server ({server.Trim()})");
        }

        if (AsInt(source.GetValue(WindowsUpdateKey, "DoNotConnectToWindowsUpdateInternetLocations")) == 1)
        {
            reasons.Add("Connections to Windows Update internet locations are blocked by policy");
        }

        if (AsInt(source.GetValue(AutoUpdateKey, "NoAutoUpdate")) == 1)
        {
            reasons.Add("Automatic updates are turned off by policy");
        }
        else if (source.GetValue(AutoUpdateKey, "AUOptions") is not null)
        {
            reasons.Add("Automatic update behavior is set by policy");
        }

        if (WufbValues.Any(name => IsSet(source.GetValue(WindowsUpdateKey, name))))
        {
            reasons.Add("Windows Update for Business policies (deferrals, pauses or target version) apply");
        }

        if (AsInt(source.GetValue(WindowsUpdateKey, "SetDisableUXWUAccess")) == 1)
        {
            reasons.Add("Access to Windows Update settings is restricted by policy");
        }

        return reasons.Count == 0 ? (false, null) : (true, string.Join("; ", reasons) + ".");
    }

    private static bool IsSet(object? value) => value switch
    {
        null => false,
        string s => !string.IsNullOrWhiteSpace(s),
        _ => AsInt(value) is > 0,
    };

    private static long? AsInt(object? value) => value switch
    {
        int i => i,
        long l => l,
        uint u => u,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };
}

[SupportedOSPlatform("windows")]
internal sealed class RegistryPolicySource : IUpdatePolicySource
{
    public object? GetValue(string subKey, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(name);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
