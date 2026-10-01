using Pickle.Abstractions;
using Pickle.Modules.Docker;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Tests;
using Terminal.Gui.Input;

namespace Pickle.Modules.Tests.Docker;

public class DockerTests
{
    private const string Ps = """
        {"Command":"\"nginx -g 'daemon off;'\"","CreatedAt":"2025-10-01 11:00:00 +0000 UTC","ID":"3f2a1b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708","Image":"nginx:1.27","Names":"web-1","Ports":"0.0.0.0:8080->80/tcp","State":"running","Status":"Up 1 hour"}
        {"Command":"\"sleep 5\"","CreatedAt":"2025-09-30 10:00:00 +0000 UTC","ID":"aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899","Image":"alpine","Names":"old-job","Ports":"","State":"exited","Status":"Exited (0) 2 days ago"}
        """;

    [Fact]
    public void ParsesContainersRunningFirstWithShortIds()
    {
        var items = DockerService.ParseContainers(Ps);

        Assert.Equal(["web-1", "old-job"], items.Select(i => i.Name));
        Assert.Equal(["3f2a1b4c5d6e", "aabbccddeeff"], items.Select(i => i.Id));
        Assert.Equal(["running", "exited"], items.Select(i => i.State));
        Assert.Contains("Ports:   0.0.0.0:8080->80/tcp", items[0].Lines);
    }

    [Fact]
    public void ParsesImagesVolumesAndComposeProjects()
    {
        var images = DockerService.ParseImages("{\"Repository\":\"nginx\",\"Tag\":\"1.27\",\"ID\":\"sha256:abc\",\"Size\":\"188MB\",\"CreatedSince\":\"2 weeks ago\"}\n{\"Repository\":\"<none>\",\"Tag\":\"<none>\",\"ID\":\"def\",\"Size\":\"5MB\",\"CreatedSince\":\"1 day ago\"}");
        var volumes = DockerService.ParseVolumes("{\"Driver\":\"local\",\"Name\":\"pgdata\"}");
        var compose = DockerService.ParseCompose("[{\"Name\":\"shop\",\"Status\":\"running(3)\",\"ConfigFiles\":\"/srv/shop/compose.yaml\"},{\"Name\":\"blog\",\"Status\":\"exited(1)\",\"ConfigFiles\":\"/srv/blog/compose.yaml\"}]");

        Assert.Equal(["<none>:<none>", "nginx:1.27"], images.Select(i => i.Name));
        Assert.Equal(["pgdata"], volumes.Select(v => v.Name));
        Assert.Equal([("blog", "stopped"), ("shop", "running")], compose.Select(c => (c.Name, c.State)));
    }

    [Fact]
    public async Task ActionsAreSingleArgumentsAndIdsAreChecked()
    {
        var runner = new FakeProgramRunner().On("docker", "ps", Ps).On("docker", "stop", string.Empty).On("docker", "rm", string.Empty);
        using var t = TestPickle.Create();
        var docker = new DockerService(runner, t.Runtime.Config);
        var web = (await docker.ListAsync(DockerKind.Containers, CancellationToken.None))[0];

        await docker.ActAsync(web, "stop", CancellationToken.None);
        await docker.ActAsync(web, "remove", CancellationToken.None);

        Assert.Equal(["ps -a --no-trunc --format {{json .}}", "stop 3f2a1b4c5d6e", "rm 3f2a1b4c5d6e"], runner.CommandLines("docker"));
        Assert.Throws<ArgumentException>(() => docker.Arguments(web with { Id = "x; rm -rf /" }, "stop"));
        Assert.Throws<ArgumentException>(() => docker.Arguments(web with { Id = "--privileged" }, "stop"));
        Assert.Throws<ArgumentException>(() => docker.Arguments(web, "destroy"));
        Assert.Equal("docker logs -f --tail 200 3f2a1b4c5d6e", docker.ShellCommand(web, "logs"));
        Assert.Equal("docker exec -it 3f2a1b4c5d6e sh", docker.ShellCommand(web, "shell"));
    }

    [Fact]
    public void PodmanIsUsedWhenDockerIsMissingAndTheSettingWins()
    {
        using var t = TestPickle.Create();
        var onlyPodman = new FakeProgramRunner().On("podman", "ps", string.Empty);

        Assert.Equal("podman", new DockerService(onlyPodman, t.Runtime.Config).Runtime);

        var both = new FakeProgramRunner().On("docker", "ps", string.Empty).On("podman", "ps", string.Empty);
        Assert.Equal("docker", new DockerService(both, t.Runtime.Config).Runtime);
        t.Runtime.Config.Set("docker", new DockerSettings { Runtime = "podman" });
        Assert.Equal("podman", new DockerService(both, t.Runtime.Config).Runtime);
        Assert.Null(new DockerService(new FakeProgramRunner(), t.Runtime.Config).Runtime);
    }

    [Fact]
    public void ThePanelListsContainersStopsOneAfterConfirmAndOpensLogsInTheShell()
    {
        var runner = new FakeProgramRunner().On("docker", "ps", Ps).On("docker", "stop", string.Empty).On("docker", "logs", "line one\nline two\n");
        var script = new UiScript()
            .WaitFor("loaded", app => TuiHarness.Top<DockerPanel>(app).List.TotalCount == 2 && TuiHarness.Top<DockerPanel>(app).List.Selected?.Name == "web-1")
            .WaitFor("details", app => TuiHarness.Top<DockerPanel>(app).Details.PlainText.Contains("line two", StringComparison.Ordinal))
            .Press(Key.F3)
            .WaitFor("dialog", app => app.TopRunnableView is Terminal.Gui.Views.Dialog)
            .Press(Key.Y)
            .WaitFor("stopped", _ => runner.CommandLines("docker").Any(c => c == "stop 3f2a1b4c5d6e"))
            .Press(Key.F8);
        var (t, host) = ModuleTestSupport.StartPanels("docker", runner, script);
        using var _ = t;

        var result = host.Show("docker");

        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, "docker logs -f --tail 200 3f2a1b4c5d6e"), result);
    }

    [Fact]
    public void ThePkCommandListsFiltersAndActs()
    {
        var runner = new FakeProgramRunner().On("docker", "ps", Ps).On("docker", "restart", string.Empty);
        using var t = ModuleTestSupport.Start("docker", runner);

        Assert.Equal(["web-1"], t.Run("pk docker list containers web | ForEach-Object Name"));
        t.Run("pk docker restart web-1 --yes");

        Assert.Contains("restart 3f2a1b4c5d6e", runner.CommandLines("docker"));
    }
}
