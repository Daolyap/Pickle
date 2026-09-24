using Pickle.Abstractions.Services;
using Pickle.Windows.Winget;

namespace Pickle.Windows.Tests.Winget;

public class WingetCliParserTests
{
    [Fact]
    public void ParsesListWithSpinnerNoiseTruncationAndLocalEntries()
    {
        var packages = WingetCliParser.ParsePackages(WingetFixtures.List, WingetTableKind.List);

        Assert.Equal(6, packages.Count);
        Assert.Equal(new WingetPackage("Git.Git", "Git", "2.45.1", "2.46.0", "winget"), packages[0]);
        Assert.True(packages[0].IsUpgradable);
        Assert.Equal("Microsoft Visual C++ 2015-2022 Redistri…", packages[1].Name);
        Assert.Equal("Microsoft.VCRedist.2015+.x64", packages[1].Id);
        Assert.Null(packages[2].AvailableVersion);
        Assert.False(packages[2].IsUpgradable);
        Assert.Equal(@"ARP\Machine\X64\Git_is1", packages[4].Id);
        Assert.Null(packages[4].Source);
        Assert.Equal("Contoso.VeryLongPackageIdentifierTha…", packages[5].Id);
        Assert.False(WindowsIds.IsValidWingetId(packages[5].Id));
    }

    [Fact]
    public void ParsesUpgradeTablesAndStopsAtFooters()
    {
        var packages = WingetCliParser.ParsePackages(WingetFixtures.Upgrade, WingetTableKind.Upgrade);

        Assert.Equal(["Git.Git", "Microsoft.PowerShell", "Spotify.Spotify"], packages.Select(p => p.Id));
        Assert.Equal("7.4.5.0", packages[1].AvailableVersion);
        Assert.Equal("7.4.2.0", packages[1].InstalledVersion);
        Assert.All(packages, p => Assert.True(p.IsUpgradable));
    }

    [Fact]
    public void ParsesSearchWithEastAsianWidthsAndMatchColumn()
    {
        var packages = WingetCliParser.ParsePackages(WingetFixtures.Search, WingetTableKind.Search);

        Assert.Equal(3, packages.Count);
        Assert.Equal(new WingetPackage("Tencent.WeChat", "微信", null, "3.9.10", "winget"), packages[0]);
        Assert.Equal(new WingetPackage("Tencent.QQMusic", "QQ音乐", null, "19.51", "winget"), packages[1]);
        Assert.Equal("Visual Studio…", packages[2].Name);
        Assert.Equal("Microsoft.VisualStudio.2022.Community", packages[2].Id);
    }

    [Fact]
    public void FallsBackToCharacterColumnsWhenPaddedByCharCount()
    {
        const string output = "Name     Id              Version\n" +
                              "---------------------------------\n" +
                              "微信       Tencent.WeChat  3.9.10\n";
        var packages = WingetCliParser.ParsePackages(output, WingetTableKind.Search);
        Assert.Equal("Tencent.WeChat", Assert.Single(packages).Id);
    }

    [Fact]
    public void UnknownLocalizedHeadersMapByPositionAndContent()
    {
        const string output = "Navn     Id        Versjon Tilgjengelig Kilde\n" +
                              "----------------------------------------------\n" +
                              "Git      Git.Git   2.45.1  2.46.0       winget\n";
        var package = Assert.Single(WingetCliParser.ParsePackages(output, WingetTableKind.Upgrade));
        Assert.Equal(new WingetPackage("Git.Git", "Git", "2.45.1", "2.46.0", "winget"), package);
    }

    [Fact]
    public void EmptyOrMessageOnlyOutputYieldsNoPackages()
    {
        Assert.Empty(WingetCliParser.ParsePackages(string.Empty, WingetTableKind.List));
        Assert.Empty(WingetCliParser.ParsePackages("No package found matching input criteria.\r\n", WingetTableKind.Search));
    }

    [Fact]
    public void ParsesShowOutput()
    {
        const string output = "   - \r   \\ \rFound Visual Studio Code [Microsoft.VisualStudioCode]\r\n" +
                              "Version: 1.93.1\r\n" +
                              "Publisher: Microsoft Corporation\r\n" +
                              "Publisher Url: https://code.visualstudio.com\r\n" +
                              "Description:\r\n" +
                              "  Visual Studio Code is a lightweight but powerful source code editor.\r\n" +
                              "  It runs on your desktop.\r\n" +
                              "Homepage: https://code.visualstudio.com\r\n" +
                              "License: Microsoft Software License\r\n" +
                              "Release Notes Url: https://code.visualstudio.com/updates/v1_93\r\n" +
                              "Tags:\r\n" +
                              "  editor\r\n" +
                              "Installer:\r\n" +
                              "  Installer Type: inno\r\n" +
                              "  Installer Url: https://update.code.visualstudio.com/1.93.1/win32-x64-user/stable\r\n";
        var details = WingetCliParser.ParseShow(output, ["1.93.1", "1.93.0"]);

        Assert.NotNull(details);
        Assert.Equal("Microsoft.VisualStudioCode", details.Id);
        Assert.Equal("Visual Studio Code", details.Name);
        Assert.Equal("1.93.1", details.LatestVersion);
        Assert.Equal("Microsoft Corporation", details.Publisher);
        Assert.Equal("https://code.visualstudio.com", details.Homepage);
        Assert.Equal("Microsoft Software License", details.License);
        Assert.Equal("Visual Studio Code is a lightweight but powerful source code editor.\nIt runs on your desktop.", details.Description);
        Assert.Equal("https://code.visualstudio.com/updates/v1_93", details.ReleaseNotes);
        Assert.Equal(["1.93.1", "1.93.0"], details.AvailableVersions);
    }

    [Fact]
    public void ShowWithoutFoundLineIsNull() =>
        Assert.Null(WingetCliParser.ParseShow("No package found matching input criteria.\r\n"));

    [Fact]
    public void ParsesVersionList()
    {
        const string output = "Found Git [Git.Git]\r\nVersion\r\n-------\r\n2.46.0\r\n2.45.2\r\n2.45.1\r\n";
        Assert.Equal(["2.46.0", "2.45.2", "2.45.1"], WingetCliParser.ParseVersions(output));
    }

    [Fact]
    public void ParsesSourceExportAndSourceList()
    {
        const string export =
            "{\"Arg\":\"https://cdn.winget.microsoft.com/cache\",\"Data\":\"Microsoft.Winget.Source_8wekyb3d8bbwe\",\"Name\":\"winget\",\"Type\":\"Microsoft.PreIndexed.Package\"}\r\n" +
            "{\"Arg\":\"https://storeedgefd.dsx.mp.microsoft.com/v9.0\",\"Name\":\"msstore\",\"Type\":\"Microsoft.Rest\"}\r\n";
        Assert.Equal(
            [new WingetSource("winget", "https://cdn.winget.microsoft.com/cache", "Microsoft.PreIndexed.Package"), new WingetSource("msstore", "https://storeedgefd.dsx.mp.microsoft.com/v9.0", "Microsoft.Rest")],
            WingetCliParser.ParseSources(export));

        const string list = "Name    Argument                                      Explicit\n" +
                            "---------------------------------------------------------------\n" +
                            "msstore https://storeedgefd.dsx.mp.microsoft.com/v9.0 false\n" +
                            "winget  https://cdn.winget.microsoft.com/cache        false\n";
        Assert.Equal(["msstore", "winget"], WingetCliParser.ParseSources(list).Select(s => s.Name));
    }

    [Theory]
    [InlineData("  ██████████▒▒▒▒▒▒▒▒▒▒  12.0 MB / 48.0 MB", 25.0)]
    [InlineData("  ██████████████████████████████  100%", 100.0)]
    [InlineData("  ███▒▒▒  45%", 45.0)]
    [InlineData("  ██▒▒  512 KB / 2.00 MB", 25.0)]
    public void ParsesProgressPercent(string segment, double expected) =>
        Assert.Equal(expected, WingetCliParser.ParsePercent(segment));

    [Fact]
    public void ProgressTrackerReportsStagesAndPercents()
    {
        var reports = new List<WingetProgress>();
        var tracker = new WingetProgressTracker(new SyncProgress<WingetProgress>(reports.Add));
        foreach (var segment in new[]
        {
            "Found Git [Git.Git] Version 2.46.0", "This application is licensed to you by its owner.",
            "Downloading https://github.com/git-for-windows/git/releases/download/Git-64-bit.exe",
            "  ██████▒▒▒▒▒▒  20.0 MB / 65.0 MB", "  ██████████████  65.0 MB / 65.0 MB",
            "Successfully verified installer hash", "Starting package install...", "  ███████  100%", "Successfully installed",
        })
        {
            tracker.Feed(segment);
        }

        Assert.Equal(["Resolving", "Downloading", "Downloading", "Downloading", "Verified", "Installing", "Installing", "Installed"], reports.Select(r => r.Stage));
        Assert.Equal(100, reports[^1].Percent);
        Assert.Equal(30.8, reports[2].Percent);
    }

    [Fact]
    public void ExitCodesMapToFriendlyResults()
    {
        var ok = WingetErrors.FromExitCode(0, "Successfully installed\r\n", "install", "Git.Git");
        Assert.True(ok.Success);
        Assert.Equal("Successfully installed", ok.Message);

        var notFound = WingetErrors.FromExitCode(unchecked((int)0x8A150014), "No package found matching input criteria.", "install", "Nope.Nope");
        Assert.False(notFound.Success);
        Assert.Contains("No package found", notFound.Message, StringComparison.Ordinal);
        Assert.Contains("0x8A150014", notFound.Message, StringComparison.Ordinal);

        var reboot = WingetErrors.FromExitCode(unchecked((int)0x8A150109), string.Empty, "install", "Foo.Bar");
        Assert.True(reboot.Success);
        Assert.True(reboot.RebootRequired);

        var unknown = WingetErrors.FromExitCode(1, "Installer failed with exit code: 1603\r\n", "install", "Foo.Bar");
        Assert.Contains("Installer failed with exit code: 1603", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ModuleStatusesMapToFriendlyResults()
    {
        Assert.True(WingetErrors.FromModuleStatus("Ok", false, "install", "Git.Git", null).Success);
        var failed = WingetErrors.FromModuleStatus("NoApplicableUpgrade", false, "upgrade", "Git.Git", "0x8A15002B");
        Assert.False(failed.Success);
        Assert.Contains("no applicable upgrade", failed.Message, StringComparison.Ordinal);
    }
}
