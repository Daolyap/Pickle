using Pickle.Core.Hosting;
using Pickle.Testing;

namespace Pickle.Core.Tests.Hosting;

public class StartupTimingsTests
{
    [Fact]
    public void PhasesAddUpAndTheSummaryNamesTheSlowest()
    {
        var timings = new StartupTimings();
        Thread.Sleep(30);
        timings.Mark("slow");
        timings.Mark("fast");

        var phases = timings.Phases.Where(p => p.Phase != "process start").ToList();
        Assert.Equal(["slow", "fast"], phases.Select(p => p.Phase));
        Assert.True(phases[0].Duration >= TimeSpan.FromMilliseconds(25), phases[0].Duration.ToString());
        Assert.Contains("slow", timings.Summary(top: 1) + timings.Summary(top: 2), StringComparison.Ordinal);
        Assert.Equal("850 ms", StartupTimings.Format(TimeSpan.FromMilliseconds(850.4)));
        Assert.Equal("1.3 s", StartupTimings.Format(TimeSpan.FromMilliseconds(1260)));
    }

    [Fact]
    public void TheRuntimeRecordsItsPhasesAndPkDoctorShowsThem()
    {
        using var t = TestPickle.Create(width: 100, height: 40, start: true);
        var phases = t.Runtime.Startup.Phases.Select(p => p.Phase).ToList();
        Assert.Contains("components", phases);
        Assert.Contains("runspace", phases);
        Assert.Contains("plugins", phases);

        t.Run("pk doctor --startup");
        var screen = t.Terminal.GetScreenText();
        Assert.Contains("runspace", screen, StringComparison.Ordinal);
        Assert.Contains("total", screen, StringComparison.Ordinal);
    }
}
