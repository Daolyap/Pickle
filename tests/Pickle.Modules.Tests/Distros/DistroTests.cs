using Pickle.Abstractions;
using Pickle.Modules.Distros;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Tests;
using Terminal.Gui.Input;

namespace Pickle.Modules.Tests.Distros;

public class DistroTests
{
    private const string WslVerbose = "  NAME            STATE           VERSION\n* Ubuntu          Running         2\n  Debian          Stopped         2\n  docker-desktop  Stopped         2\n";

    private const string DistroboxList = """
        ID           | NAME                 | STATUS             | IMAGE
        a1b2c3d4e5f6 | ubuntu               | Up 2 hours         | docker.io/library/ubuntu:24.04
        0f9e8d7c6b5a | fedora-dev           | Exited (0) 3 days ago | registry.fedoraproject.org/fedora-toolbox:40
        """;

    private const string Lxc = """
        [{"name":"web","status":"Running","type":"container","architecture":"x86_64","config":{"image.description":"Ubuntu 24.04 LTS amd64"},"state":{"network":{"lo":{"addresses":[{"family":"inet","address":"127.0.0.1"}]},"eth0":{"addresses":[{"family":"inet","address":"10.1.2.3"},{"family":"inet6","address":"fd42::1"}]}}}},
         {"name":"old","status":"Stopped","type":"virtual-machine","architecture":"aarch64","config":{},"state":null}]
        """;

    private const string Podman = """
        [{"Id":"abcdef0123456789","Names":["fedora-toolbox-40"],"Image":"registry.fedoraproject.org/fedora-toolbox:40","State":"running"},
         {"Id":"123456789abcdef0","Names":["old-box"],"Image":"quay.io/toolbx/arch-toolbox","State":"exited"}]
        """;

    private static string Utf16Mangled(string text) => "\uFFFD\uFFFD" + string.Join("\0", text.Select(c => c.ToString())) + "\0";

    // ───────────── parsing ─────────────

    [Fact]
    public void WslListsDistributionsDefaultFirstAndStateComesFromTheRunningList()
    {
        var items = WslBackend.Parse(WslVerbose, "Ubuntu\n");

        Assert.Equal(["Ubuntu", "Debian", "docker-desktop"], items.Select(i => i.Name));
        Assert.Equal(["running", "stopped", "stopped"], items.Select(i => i.State));
        Assert.True(items[0].IsDefault);
        Assert.Equal("WSL 2 · default", items[0].Summary);
        Assert.Contains("Version: WSL 2", items[1].Lines);
    }

    [Fact]
    public void WslStateIsReadFromTheTableWhenThereIsNoRunningList()
    {
        Assert.Equal(["running", "stopped"], WslBackend.Parse(WslVerbose, null).Take(2).Select(i => i.State));
    }

    [Fact]
    public void ATranslatedStateColumnDoesNotMatter()
    {
        const string german = "  NAME     STATUS      VERSION\n* Ubuntu   Wird ausgeführt  2\n";

        Assert.Equal("running", WslBackend.Parse(german.Replace("Wird ausgeführt", "Ausgeführt", StringComparison.Ordinal), "Ubuntu").Single().State);
        Assert.Equal("stopped", WslBackend.Parse(german.Replace("Wird ausgeführt", "Ausgeführt", StringComparison.Ordinal), "Es sind keine Distributionen aktiv.").Single().State);
    }

    [Fact]
    public void Utf16OutputReadAsUtf8IsCleanedUp()
    {
        var items = WslBackend.Parse(Utf16Mangled(WslVerbose), Utf16Mangled("Debian\n"));

        Assert.Equal(["Ubuntu", "Debian", "docker-desktop"], items.Select(i => i.Name));
        Assert.Equal("running", items.Single(i => i.Name == "Debian").State);
    }

    [Fact]
    public void WslWithNoDistributionsListsNothing()
    {
        Assert.Empty(WslBackend.Parse("Windows Subsystem for Linux has no installed distributions.\n", null));
    }

    [Fact]
    public void OnlineDistributionsAreParsedFromTheirTable()
    {
        const string text = "The following is a list of valid distributions that can be installed.\nInstall using 'wsl.exe --install <Distro>'.\n\nNAME                            FRIENDLY NAME\nUbuntu                          Ubuntu\nDebian                          Debian GNU/Linux\nUbuntu-24.04                    Ubuntu 24.04 LTS\n";

        Assert.Equal([("Ubuntu", "Ubuntu"), ("Debian", "Debian GNU/Linux"), ("Ubuntu-24.04", "Ubuntu 24.04 LTS")], WslBackend.ParseOnline(text).Select(o => (o.Name, o.Description)));
    }

    [Fact]
    public void DistroboxRowsBecomeContainersRunningFirst()
    {
        var items = DistroboxBackend.Parse(DistroboxList);

        Assert.Equal(["ubuntu", "fedora-dev"], items.Select(i => i.Name));
        Assert.Equal(["running", "stopped"], items.Select(i => i.State));
        Assert.Equal("docker.io/library/ubuntu:24.04", items[0].Summary);
    }

    [Fact]
    public void ToolboxContainersComeFromPodmanJson()
    {
        var items = ToolboxBackend.Parse(Podman);

        Assert.Equal(["fedora-toolbox-40", "old-box"], items.Select(i => i.Name));
        Assert.Equal(["running", "stopped"], items.Select(i => i.State));
        Assert.Contains("Id:     abcdef012345", items[0].Lines);
        Assert.Empty(ToolboxBackend.Parse("not json"));
    }

    [Fact]
    public void LxcInstancesShowTypeImageAndTheirAddress()
    {
        var items = LxcBackend.Parse(Lxc, "lxc");

        Assert.Equal(["web", "old"], items.Select(i => i.Name));
        Assert.Equal("container · Ubuntu 24.04 LTS amd64", items[0].Summary);
        Assert.Contains("Address:  10.1.2.3", items[0].Lines);
        Assert.DoesNotContain(items[0].Lines, l => l.Contains("127.0.0.1", StringComparison.Ordinal) || l.Contains("fd42", StringComparison.Ordinal));
        Assert.Equal("virtual-machine", items[1].Summary);
    }

    // ───────────── commands are argument lists ─────────────

    private static Distro Make(string backend, string name, string state = "running") => new(backend, name, state, string.Empty, false, []);

    [Fact]
    public void WslActionsAreFixedArgumentLists()
    {
        var wsl = new WslBackend(() => new FakeProgramRunner(), new FixedClock());
        var ubuntu = Make("wsl", "Ubuntu");

        Assert.Equal(["--terminate", "Ubuntu"], wsl.Command(ubuntu, "stop")!.Arguments);
        Assert.Equal(["--unregister", "Ubuntu"], wsl.Command(ubuntu, "remove")!.Arguments);
        Assert.Equal(["--set-default", "Ubuntu"], wsl.Command(ubuntu, "default")!.Arguments);
        Assert.Null(wsl.Command(ubuntu, "start"));
        Assert.Equal("1", wsl.Command(ubuntu, "stop")!.Environment!["WSL_UTF8"]);
        Assert.Equal("wsl.exe -d Ubuntu", wsl.ShellLine(ubuntu, "enter"));
        Assert.Equal("wsl.exe --export Ubuntu Ubuntu-20261001.tar", wsl.ShellLine(ubuntu, "export"));
        Assert.Equal("wsl.exe --install -d Ubuntu-24.04", wsl.CreateLine("Ubuntu-24.04", null));
        Assert.Equal(["--import", "dev", "D:\\wsl\\dev", "D:\\dev.tar", "--version", "2"], WslBackend.Import("dev", "D:\\wsl\\dev", "D:\\dev.tar", 2).Arguments);
        Assert.Equal(["--set-version", "dev", "1"], WslBackend.SetVersion("dev", 1).Arguments);
        Assert.Throws<ArgumentException>(() => WslBackend.SetVersion("dev", 3));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public void ContainerActionsAreFixedArgumentLists()
    {
        var runner = () => new FakeProgramRunner();
        var box = new DistroboxBackend(runner);
        var toolbox = new ToolboxBackend(runner);
        var lxc = new LxcBackend(runner, "lxc");
        var incus = new LxcBackend(runner, "incus");

        Assert.Equal(["stop", "--yes", "dev"], box.Command(Make("distrobox", "dev"), "stop")!.Arguments);
        Assert.Equal(["enter", "dev", "--", "true"], box.Command(Make("distrobox", "dev"), "start")!.Arguments);
        Assert.Equal(["rm", "--force", "dev"], box.Command(Make("distrobox", "dev"), "remove")!.Arguments);
        Assert.Equal("distrobox enter dev", box.ShellLine(Make("distrobox", "dev"), "enter"));
        Assert.Equal("distrobox create --yes --name dev --image ubuntu:24.04", box.CreateLine("dev", "ubuntu:24.04"));
        Assert.Equal("distrobox create --yes --name dev", box.CreateLine("dev", null));

        Assert.Equal(("podman", "restart"), (toolbox.Command(Make("toolbox", "t"), "restart")!.Program, toolbox.Command(Make("toolbox", "t"), "restart")!.Arguments[0]));
        Assert.Equal(["rm", "--force", "t"], toolbox.Command(Make("toolbox", "t"), "remove")!.Arguments);
        Assert.Equal("toolbox create --image fedora-toolbox:40 t", toolbox.CreateLine("t", "fedora-toolbox:40"));

        Assert.Equal(["delete", "--force", "web"], lxc.Command(Make("lxc", "web"), "remove")!.Arguments);
        Assert.Equal("incus", incus.Command(Make("incus", "web"), "stop")!.Program);
        Assert.Equal("lxc launch ubuntu:24.04 web", lxc.CreateLine("web", "ubuntu:24.04"));
        Assert.Equal("lxc exec web -- sh -c 'exec bash -l 2>/dev/null || exec sh -l'", lxc.ShellLine(Make("lxc", "web"), "enter"));
        Assert.Throws<ArgumentException>(() => lxc.CreateLine("web", null));
    }

    [Theory]
    [InlineData("--privileged")]
    [InlineData("-rf")]
    [InlineData("a b")]
    [InlineData("dev; rm -rf /")]
    [InlineData("")]
    [InlineData("$(whoami)")]
    public void NamesThatCouldBeOptionsOrScriptAreRefusedEverywhere(string name)
    {
        var runner = () => new FakeProgramRunner();
        IDistroBackend[] backends = [new WslBackend(runner), new DistroboxBackend(runner), new ToolboxBackend(runner), new LxcBackend(runner)];

        foreach (var backend in backends)
        {
            Assert.Throws<ArgumentException>(() => backend.Command(Make(backend.Id, name), "stop"));
            Assert.Throws<ArgumentException>(() => backend.ShellLine(Make(backend.Id, name), "enter"));
            Assert.Throws<ArgumentException>(() => backend.CreateLine(name, "ubuntu:24.04"));
        }
    }

    [Theory]
    [InlineData("--privileged")]
    [InlineData("a b")]
    [InlineData("x;y")]
    public void ImagesThatCouldBeOptionsOrScriptAreRefused(string image) =>
        Assert.Throws<ArgumentException>(() => new DistroboxBackend(() => new FakeProgramRunner()).CreateLine("dev", image));

    [Fact]
    public void PathsMayNotStartWithADash() =>
        Assert.Throws<ArgumentException>(() => WslBackend.Export("dev", "--shutdown"));

    // ───────────── service ─────────────

    private static FakeProgramRunner Everything() => new FakeProgramRunner()
        .On("wsl.exe", "--list --verbose", WslVerbose)
        .On("wsl.exe", "--list --running --quiet", "Ubuntu\n")
        .On("wsl.exe", "--terminate", string.Empty)
        .On("distrobox", "list", DistroboxList)
        .On("distrobox", "stop", string.Empty);

    private static DistroService Service(FakeProgramRunner runner)
    {
        Func<Pickle.Abstractions.Services.IProgramRunner> factory = () => runner;
        return new DistroService([new WslBackend(factory), new DistroboxBackend(factory), new ToolboxBackend(factory), new LxcBackend(factory, "lxc")], factory);
    }

    [Fact]
    public async Task OnlyInstalledBackendsAreListedAndOneFailingDoesNotHideTheOthers()
    {
        var runner = Everything();
        runner.On("lxc", (_, _) => new Pickle.Abstractions.Services.ProgramResult(1, string.Empty, "Error: LXD unix socket not accessible: permission denied"));
        var service = Service(runner);

        var listing = await service.ListAsync(default);

        Assert.Equal(["wsl", "wsl", "wsl", "distrobox", "distrobox"], listing.Items.Select(i => i.Backend));
        Assert.Equal(["LXC: Error: LXD unix socket not accessible: permission denied"], listing.Errors);
        Assert.DoesNotContain(runner.Calls, c => c.Program.EndsWith("podman", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnActionRunsItsArgumentListAndReportsTheOutcome()
    {
        var runner = Everything();
        var service = Service(runner);
        var items = (await service.ListAsync(default)).Items;

        var stopped = await service.ActAsync(items.First(i => i.Name == "Ubuntu"), "stop", default);
        var unsupported = await service.ActAsync(items.First(i => i.Name == "Ubuntu"), "start", default);

        Assert.True(stopped.Success);
        Assert.Equal("stop Ubuntu: done.", stopped.Message);
        Assert.Contains("--terminate Ubuntu", runner.CommandLines("wsl.exe"));
        Assert.False(unsupported.Success);
        Assert.Contains("no 'start'", unsupported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedActionShowsTheCleanedMessage()
    {
        var runner = new FakeProgramRunner().On("wsl.exe", "--terminate", string.Empty, exitCode: 1, stderr: Utf16Mangled("There is no distribution with the supplied name."));
        var service = Service(runner);

        var result = await service.ActAsync(Make("wsl", "Ghost"), "stop", default);

        Assert.False(result.Success);
        Assert.Equal("There is no distribution with the supplied name.", result.Message);
    }

    [Fact]
    public void FindMatchesCaseInsensitivelyAndAsksForABackendWhenAmbiguous()
    {
        List<Distro> distros = [Make("wsl", "Dev"), Make("distrobox", "dev"), Make("distrobox", "other")];

        Assert.Equal("other", DistroService.Find(distros, "OTHER")!.Name);
        Assert.Null(DistroService.Find(distros, "missing"));
        Assert.Equal("distrobox", DistroService.Find(distros, "dev", "distrobox")!.Backend);
        Assert.Contains("--backend", Assert.Throws<ArgumentException>(() => DistroService.Find(distros, "dev")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WslOutputInUtf16WorksEndToEndThroughTheService()
    {
        var runner = new FakeProgramRunner()
            .On("wsl.exe", "--list --verbose", Utf16Mangled(WslVerbose))
            .On("wsl.exe", "--list --running --quiet", Utf16Mangled("Ubuntu\n"));

        var listing = await Service(runner).ListAsync(default);

        Assert.Equal(["Ubuntu", "Debian", "docker-desktop"], listing.Items.Select(i => i.Name));
        Assert.All(runner.Calls, c => Assert.Equal("1", c.Options!.Environment!["WSL_UTF8"]));
    }

    [Fact]
    public async Task ARunningWslDistributionIsDescribedFromItsOsRelease()
    {
        var runner = Everything().On("wsl.exe", "-d Ubuntu --exec cat /etc/os-release", "NAME=\"Ubuntu\"\nPRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\n");
        var service = Service(runner);
        var ubuntu = (await service.ListAsync(default)).Items.First(i => i.Name == "Ubuntu");
        var debian = (await service.ListAsync(default)).Items.First(i => i.Name == "Debian");

        var running = await service.DescribeAsync(ubuntu, default);
        var stopped = await service.DescribeAsync(debian, default);

        Assert.Contains("System:  Ubuntu 24.04.1 LTS", running);
        Assert.Contains(stopped, l => l.StartsWith("Stopped:", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.CommandLines("wsl.exe"), l => l.Contains("-d Debian", StringComparison.Ordinal));
    }

    // ───────────── pk wsl in a real shell ─────────────

    [Fact]
    public void PkListsFiltersAndSkipsMissingBackends()
    {
        using var t = ModuleTestSupport.Start("wsl", Everything());

        Assert.Equal(["Debian"], t.Run("pk wsl list deb | ForEach-Object Name"));
        Assert.Equal(["ubuntu:running", "fedora-dev:stopped"], t.Run("pk distros list --backend distrobox | ForEach-Object { \"$($_.Name):$($_.State)\" }"));
    }

    [Fact]
    public void StoppingAsksFirstUnlessYes()
    {
        var runner = Everything();
        using var t = ModuleTestSupport.Start("wsl", runner);

        t.Terminal.Type("n").Press("Enter");
        t.Run("pk wsl stop Ubuntu --backend wsl");
        Assert.DoesNotContain("--terminate Ubuntu", runner.CommandLines("wsl.exe"));

        t.Run("pk wsl stop Ubuntu --backend wsl --yes");
        Assert.Contains("--terminate Ubuntu", runner.CommandLines("wsl.exe"));
    }

    [Fact]
    public void RemovingNeverHappensWithoutAnAnswer()
    {
        var runner = Everything().On("wsl.exe", "--unregister", string.Empty);
        using var t = ModuleTestSupport.Start("wsl", runner);

        t.Terminal.Type("n").Press("Enter");
        t.Run("pk wsl remove Debian");
        Assert.DoesNotContain(runner.CommandLines("wsl.exe"), l => l.StartsWith("--unregister", StringComparison.Ordinal));

        t.Run("pk wsl rm Debian --yes");
        Assert.Contains("--unregister Debian", runner.CommandLines("wsl.exe"));
    }

    [Fact]
    public void EnterQueuesTheShellLineAndUnknownNamesFail()
    {
        using var t = ModuleTestSupport.Start("wsl", Everything());

        t.Run("pk wsl enter ubuntu --backend distrobox");
        t.Run("pk wsl enter nothing-here");

        Assert.Equal(["distrobox enter ubuntu"], t.Runtime.Engine.SubmittedCommands);
    }

    [Fact]
    public void NewNeedsABackendWhenSeveralAreAvailable()
    {
        using var t = ModuleTestSupport.Start("wsl", Everything());

        Assert.NotNull(Record.Exception(() => t.Run("pk wsl new dev")));
        t.Run("pk wsl new dev --image ubuntu:24.04 --backend distrobox");

        Assert.Equal(["distrobox create --yes --name dev --image ubuntu:24.04"], t.Runtime.Engine.SubmittedCommands);
    }

    [Fact]
    public void ExportAndImportStreamTheirOutput()
    {
        var runner = Everything();
        runner.StreamLines.AddRange(["Export in progress, this may take a few minutes.", "The operation completed successfully."]);
        using var t = ModuleTestSupport.Start("wsl", runner);
        var dir = Path.GetTempPath();
        var tar = Path.Combine(dir, "pickle-test-" + Guid.NewGuid().ToString("N") + ".tar");
        File.WriteAllText(tar, "x");
        try
        {
            t.Run($"pk wsl export Ubuntu '{Path.Combine(dir, "out-" + Guid.NewGuid().ToString("N") + ".tar")}'");
            t.Run($"pk wsl import copy '{Path.Combine(dir, "wsl-copy")}' '{tar}' --version 2");

            var screen = t.Terminal.GetScreenText();
            Assert.Contains("The operation completed successfully.", screen, StringComparison.Ordinal);
            Assert.Contains(runner.Calls, c => c.Arguments[0] == "--export" && c.Arguments[1] == "Ubuntu");
            Assert.Contains(runner.Calls, c => c.Arguments is ["--import", "copy", _, _, "--version", "2"]);
        }
        finally
        {
            File.Delete(tar);
        }
    }

    [Fact]
    public void ShutdownIsConfirmedAndWslCommandsFailCleanlyWithoutWsl()
    {
        var runner = new FakeProgramRunner().On("distrobox", "list", DistroboxList);
        using var t = ModuleTestSupport.Start("wsl", runner);

        t.Run("pk wsl shutdown --yes");

        Assert.DoesNotContain(runner.Calls, c => c.Program.EndsWith("wsl.exe", StringComparison.Ordinal));
        Assert.Contains("wsl.exe", t.Terminal.GetScreenText(), StringComparison.Ordinal);
    }

    // ───────────── the panel ─────────────

    [Fact]
    public void ThePanelGroupsByBackendStopsAfterConfirmAndEntersInTheShell()
    {
        var runner = Everything();
        var script = new UiScript()
            .WaitFor("loaded", app => TuiHarness.Top<DistroPanel>(app).List.TotalCount == 5 && TuiHarness.Top<DistroPanel>(app).List.Selected?.Name == "Ubuntu")
            .Press(Key.F3)
            .WaitFor("dialog", app => app.TopRunnableView is Terminal.Gui.Views.Dialog)
            .Press(Key.Y)
            .WaitFor("stopped", _ => runner.CommandLines("wsl.exe").Contains("--terminate Ubuntu"))
            .Press(Key.F9);
        var (t, host) = ModuleTestSupport.StartPanels("wsl", runner, script);
        using var _ = t;

        var result = host.Show("distros");

        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, "wsl.exe -d Ubuntu"), result);
    }

    [Fact]
    public void ThePanelExplainsWhenNothingIsInstalled()
    {
        var script = new UiScript()
            .WaitFor("message", app => TuiHarness.Top<DistroPanel>(app).Details.PlainText.Contains("None of wsl.exe", StringComparison.Ordinal))
            .Press(Key.Esc);
        var (t, host) = ModuleTestSupport.StartPanels("wsl", new FakeProgramRunner(), script);
        using var _ = t;

        host.Show("distros");

        script.AssertOk();
    }

    [Fact]
    public void TheModuleIsOffUntilSelectedAndNamesTheToolsItDrives()
    {
        using var t = TestPickle.Create(start: true, modules: OptionalModules.All);

        Assert.Null(t.Runtime.CommandRegistry.Get("wsl"));
        Assert.Contains("wsl.exe", WslModule.Descriptor.Tools);
    }
}
