using Pickle.Core.SystemMonitoring;

namespace Pickle.Core.Tests.SystemMonitoring;

public sealed class SystemInfoTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pickle-sysinfo").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ParsesProcFiles()
    {
        Assert.Equal([0.52, 0.58, 0.59], SystemInfoProvider.ParseLoadAverage("0.52 0.58 0.59 1/467 12345\n"));
        Assert.Null(SystemInfoProvider.ParseLoadAverage("garbage"));
        Assert.Equal("Intel Core i7-1165G7 @ 2.80GHz", SystemInfoProvider.ParseCpuModel("processor\t: 0\nmodel name\t: Intel(R) Core(TM)  i7-1165G7 @ 2.80GHz\n"));
        Assert.Equal("Raspberry Pi 4 Model B Rev 1.4", SystemInfoProvider.ParseCpuModel("processor\t: 0\nModel\t\t: Raspberry Pi 4 Model B Rev 1.4\n"));
        Assert.Equal((1024L * 1024, 4096L * 1024), SystemInfoProvider.ParseSwap("MemTotal: 1 kB\nSwapTotal:    4096 kB\nSwapFree:     3072 kB\n"));
        Assert.Null(SystemInfoProvider.ParseSwap("SwapTotal: 0 kB\nSwapFree: 0 kB\n"));
        Assert.Equal("Fedora Linux 42 (Workstation Edition)", SystemInfoProvider.ParseOsRelease("NAME=\"Fedora Linux\"\nPRETTY_NAME=\"Fedora Linux 42 (Workstation Edition)\"\n"));
    }

    [Theory]
    [InlineData("Windows 10 Pro", "24H2", 26100, "Windows 11 Pro 24H2 (build 26100)")]
    [InlineData("Windows 10 Enterprise", "22H2", 19045, "Windows 10 Enterprise 22H2 (build 19045)")]
    [InlineData(null, null, null, "Windows")]
    public void NamesWindowsLikeSettingsDoes(string? product, string? display, int? build, string expected) =>
        Assert.Equal(expected, SystemInfoProvider.WindowsName(product, display, build));

    [Fact]
    public void ReadsBatteriesFromSysfs()
    {
        Supply("AC", ("type", "Mains"), ("online", "1"));
        Supply("BAT0", ("type", "Battery"), ("capacity", "84"), ("status", "Charging"));

        var battery = SystemInfoProvider.ParseLinuxPowerSupply(_root);

        Assert.Equal((84, true, true), (battery?.Percent, battery?.Charging, battery?.OnAcPower));
        Assert.Null(SystemInfoProvider.ParseLinuxPowerSupply(Path.Combine(_root, "missing")));
    }

    [Fact]
    public async Task TheRealProviderAnswers()
    {
        var summary = await new SystemInfoProvider().GetSummaryAsync();
        Assert.Equal(Environment.MachineName, summary.MachineName);
        Assert.True(summary.LogicalProcessors > 0);
        Assert.False(string.IsNullOrWhiteSpace(summary.OperatingSystem));
    }

    private void Supply(string name, params (string File, string Value)[] files)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        foreach (var (file, value) in files)
        {
            File.WriteAllText(Path.Combine(dir, file), value + "\n");
        }
    }
}
