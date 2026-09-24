using System.Text;
using Pickle.Windows.Winget;

namespace Pickle.Windows.Tests.Winget;

public class WingetSourceRepairTests
{
    internal const string InUse = """
        Add-AppxPackage -Path https://cdn.winget.microsoft.com/cache/source.msix -ForceApplicationShutdown
        Account: DESKTOP-1\me
        Add-AppxPackage : Deployment failed with HRESULT: 0x80073D02, The package could not be installed because resources it modifies are currently in use.
        error 0x80073D02: Unable to install because the following apps need to be closed Microsoft.Winget.Source_2026.924.1.1_neutral__8wekyb3d8bbwe.
        NOTE: For additional information, look for [ActivityId] 1c4a5e8b-0000-4b8f-9b5e-7a0c2d3e4f5a in the Event Log or use the command line Get-AppxLog -ActivityID 1c4a5e8b-0000-4b8f-9b5e-7a0c2d3e4f5a
        At line:9 char:9
        +         Add-AppxPackage -Path $url -ForceApplicationShutdown -ErrorAction ...
        +         ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
            + CategoryInfo          : WriteError: (https://cdn.win...he/source.msix:String) [Add-AppxPackage], IOException
            + FullyQualifiedErrorId : DeploymentError,Microsoft.Windows.Appx.PackageManager.Commands.AddAppxPackageCommand
        --- Get-AppxLog -ActivityId 1c4a5e8b-0000-4b8f-9b5e-7a0c2d3e4f5a ---
        12:01:02 error 0x80073D02: Unable to install because the following apps need to be closed WindowsPackageManagerServer.exe.
        PICKLE-REPAIR: FAIL 0x80131620
        """;

    [Fact]
    public void TheScriptIsFixedAndTravelsEncoded()
    {
        var args = WingetSourceRepair.Arguments();
        Assert.Equal(["-NoProfile", "-NonInteractive", "-EncodedCommand"], args.Take(3));
        Assert.Equal(WingetSourceRepair.Script, Encoding.Unicode.GetString(Convert.FromBase64String(args[3])));
        Assert.Contains("Add-AppxPackage -Path $url -ForceApplicationShutdown", WingetSourceRepair.Script, StringComparison.Ordinal);
        Assert.Contains("$url = 'https://cdn.winget.microsoft.com/cache/source.msix'", WingetSourceRepair.Script, StringComparison.Ordinal);
        Assert.Contains("PSModulePath", WingetSourceRepair.Environment().Keys);
    }

    [Fact]
    public void SuccessKeepsTheOutput()
    {
        var result = WingetSourceRepair.Interpret(0, "Add-AppxPackage -Path x\r\nAccount: PC\\me\r\nPICKLE-REPAIR: OK\r\n", false, "for the current user");
        Assert.True(result.Success);
        Assert.Equal("The winget source package was re-registered for the current user.", result.Message);
        Assert.Equal("Add-AppxPackage -Path x\nAccount: PC\\me", result.Output);
    }

    [Fact]
    public void ResourcesInUseExplainsAndShowsTheLog()
    {
        var result = WingetSourceRepair.Interpret(1, InUse, false, "for the current user");
        Assert.False(result.Success);
        Assert.Equal(unchecked((int)0x80073D02), result.ExitCode);
        Assert.Contains("The source package is in use", result.Message, StringComparison.Ordinal);
        Assert.EndsWith("(0x80073D02)", result.Message, StringComparison.Ordinal);
        Assert.Contains("Get-AppxLog -ActivityId 1c4a5e8b", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("PICKLE-REPAIR", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AHigherInstalledVersionCountsAsSuccess()
    {
        var output = InUse.Replace("0x80073D02, The package could not be installed because resources it modifies are currently in use.", "0x80073D06, The package could not be installed because a higher version of this package is already installed.", StringComparison.Ordinal);
        var result = WingetSourceRepair.Interpret(1, output, false, "for the current user");
        Assert.True(result.Success);
        Assert.Contains("newer winget source package is already installed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownFailuresQuoteTheError()
    {
        var output = "#< CLIXML\n<Objs Version=\"1.1.0.1\"></Objs>\nAdd-AppxPackage : Something odd happened.\nPICKLE-REPAIR: FAIL 0x80004005\n";
        var result = WingetSourceRepair.Interpret(1, output, false, "for the current user");
        Assert.False(result.Success);
        Assert.Equal("Repairing the winget source failed: Something odd happened. (0x80004005)", result.Message);
        Assert.DoesNotContain("CLIXML", result.Output, StringComparison.Ordinal);

        var crashed = WingetSourceRepair.Interpret(-196608, string.Empty, false, "for the current user");
        Assert.False(crashed.Success);
        Assert.Contains("exit code -196608", crashed.Message, StringComparison.Ordinal);

        var slow = WingetSourceRepair.Interpret(-1, "Add-AppxPackage -Path x", true, "for the current user");
        Assert.False(slow.Success);
        Assert.Contains("timed out", slow.Message, StringComparison.Ordinal);
        Assert.Equal("Add-AppxPackage -Path x", slow.Output);
    }
}
