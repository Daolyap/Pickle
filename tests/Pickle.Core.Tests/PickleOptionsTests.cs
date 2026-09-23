namespace Pickle.Core.Tests;

public class PickleOptionsTests
{
    [Fact]
    public void CommandTakesTheRestOfTheArguments()
    {
        var o = PickleOptions.Parse(["--no-logo", "-c", "Get-Date", "-Format", "yyyy"]);
        Assert.True(o.NoLogo);
        Assert.Equal("Get-Date -Format yyyy", o.Command);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void ScriptPathIsPositional()
    {
        var o = PickleOptions.Parse(["build.ps1", "-Release", "x"]);
        Assert.Equal("build.ps1", o.File);
        Assert.Equal(["-Release", "x"], o.FileArguments);
    }

    [Fact]
    public void TerminalFragmentOptions()
    {
        var o = PickleOptions.Parse(["--write-terminal-fragment", "out.json", "--fragment-commandline", "pickle.exe"]);
        Assert.Equal("out.json", o.WriteTerminalFragment);
        Assert.Equal("pickle.exe", o.FragmentCommandLine);
    }

    [Fact]
    public void ElevatedHelperTakesPipeAndNonce()
    {
        var o = PickleOptions.Parse(["--elevated-helper", "pipe-1", "abc"]);
        Assert.Equal("pipe-1", o.ElevatedHelperPipe);
        Assert.Equal("abc", o.ElevatedHelperNonce);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--log-level")]
    public void ReportsErrors(string arg) => Assert.NotEmpty(PickleOptions.Parse([arg]).Errors);
}
