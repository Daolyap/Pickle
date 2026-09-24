using Pickle.Testing;

namespace Pickle.Core.Tests.Hosting;

public class EngineSmokeTests
{
    [Fact]
    public void RunsPowerShellAndAutoloadsModules()
    {
        using var t = TestPickle.Create(start: true);
        var output = t.Run("(1..3 | Measure-Object -Sum).Sum; Get-Date -Year 2020 -Format yyyy");
        Assert.Equal(["6", "2020"], output);
    }

    [Fact]
    public void InteractiveExecutionReportsStatusAndExitCode()
    {
        using var t = TestPickle.Create(start: true);
        var ok = t.Runtime.Engine.ExecuteInteractive("$null = 1");
        Assert.True(ok.Success);

        var failed = t.Runtime.Engine.ExecuteInteractive("Get-Item -LiteralPath '/definitely/not/here'");
        Assert.False(failed.Success);
        Assert.Contains("definitely", t.Terminal.GetScreenText(), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteHostGoesThroughHostUi()
    {
        using var t = TestPickle.Create(start: true);
        t.Runtime.Engine.ExecuteInteractive("Write-Host 'hi there' -ForegroundColor Green");
        Assert.Contains("«fg=brightGreen»hi there«»", t.Terminal.GetStyledScreen(), StringComparison.Ordinal);
    }

    [Fact]
    public void PkDispatchesRegisteredCommands()
    {
        using var t = TestPickle.Create(start: true);
        var output = t.Run("(pk version).Pickle");
        Assert.Equal([PickleRuntime.Version], output);
    }

    [Fact]
    public void LocationChangesAreTracked()
    {
        using var t = TestPickle.Create(start: true);
        var dir = Directory.CreateTempSubdirectory("pickle-cwd").FullName;
        t.Runtime.Engine.ExecuteInteractive($"Set-Location -LiteralPath '{dir}'");
        Assert.Equal(Path.GetFullPath(dir), Path.GetFullPath(t.Runtime.Engine.CurrentDirectory));
    }

    [Fact]
    public async Task BackgroundTargetRunsOnThePool()
    {
        using var t = TestPickle.Create(start: true);
        var result = await t.Runtime.Shell.InvokeAsync("param($n) $n * 3", new Dictionary<string, object?> { ["n"] = 14 }, Pickle.Abstractions.ShellTarget.Background);
        Assert.False(result.HadErrors);
        Assert.Equal("42", result.Output.Single().ToString());
    }

    [Fact]
    public void PanelsRequestedDuringAPipelineAreQueued()
    {
        using var t = TestPickle.Create(start: true);
        var panel = new Pickle.Abstractions.PanelDescriptor { Id = "p", Title = "P", Description = "d", CreateView = _ => new object() };
        Assert.False(t.Runtime.Shell.IsBusy);
        t.Runtime.Shell.OpenPanelWhenIdle(panel, "arg");
        Assert.True(t.Runtime.Engine.PendingPanels.TryDequeue(out var pending));
        Assert.Same(panel, pending.Panel);
        Assert.Equal("arg", pending.Argument);
    }
}
