using Pickle.Windows.WindowsUpdate;

namespace Pickle.Windows.Tests.WindowsUpdate;

public class UpdatePolicyTests
{
    private sealed class FakePolicy : IUpdatePolicySource
    {
        public Dictionary<(string Key, string Name), object> Values { get; } = [];

        public object? GetValue(string subKey, string name) => Values.TryGetValue((subKey, name), out var v) ? v : null;
    }

    private const string Wu = UpdatePolicyEvaluator.WindowsUpdateKey;
    private const string Au = UpdatePolicyEvaluator.AutoUpdateKey;

    [Fact]
    public void UnmanagedWhenNoPolicies() => Assert.Equal((false, null), UpdatePolicyEvaluator.Evaluate(new FakePolicy()));

    [Fact]
    public void DetectsWsus()
    {
        var policy = new FakePolicy();
        policy.Values[(Au, "UseWUServer")] = 1;
        policy.Values[(Wu, "WUServer")] = "https://wsus.contoso.local:8531";
        var (managed, reason) = UpdatePolicyEvaluator.Evaluate(policy);
        Assert.True(managed);
        Assert.Contains("wsus.contoso.local", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WsusServerWithoutUseWUServerIsIgnored()
    {
        var policy = new FakePolicy();
        policy.Values[(Wu, "WUServer")] = "https://wsus";
        policy.Values[(Au, "UseWUServer")] = 0;
        Assert.False(UpdatePolicyEvaluator.Evaluate(policy).Managed);
    }

    [Theory]
    [InlineData(Wu, "DoNotConnectToWindowsUpdateInternetLocations", 1, "blocked")]
    [InlineData(Au, "NoAutoUpdate", 1, "turned off")]
    [InlineData(Au, "AUOptions", 4, "set by policy")]
    [InlineData(Wu, "DeferQualityUpdatesPeriodInDays", 7, "Business")]
    [InlineData(Wu, "TargetReleaseVersion", 1, "Business")]
    [InlineData(Wu, "SetDisableUXWUAccess", 1, "restricted")]
    public void DetectsPolicies(string key, string name, int value, string expected)
    {
        var policy = new FakePolicy();
        policy.Values[(key, name)] = value;
        var (managed, reason) = UpdatePolicyEvaluator.Evaluate(policy);
        Assert.True(managed);
        Assert.Contains(expected, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroValuedDeferralsAreNotManaged()
    {
        var policy = new FakePolicy();
        policy.Values[(Wu, "DeferQualityUpdatesPeriodInDays")] = 0;
        policy.Values[(Wu, "DoNotConnectToWindowsUpdateInternetLocations")] = 0;
        Assert.False(UpdatePolicyEvaluator.Evaluate(policy).Managed);
    }

    [Fact]
    public void CombinesReasons()
    {
        var policy = new FakePolicy();
        policy.Values[(Au, "NoAutoUpdate")] = 1;
        policy.Values[(Wu, "PauseQualityUpdatesStartTime")] = "2026-09-01";
        var (_, reason) = UpdatePolicyEvaluator.Evaluate(policy);
        Assert.Contains("; ", reason, StringComparison.Ordinal);
    }
}
