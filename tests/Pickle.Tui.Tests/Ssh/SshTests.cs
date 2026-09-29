using Pickle.Abstractions;
using Pickle.Testing;
using Pickle.Tui.Panels.Ssh;
using Terminal.Gui.Input;

namespace Pickle.Tui.Tests.Ssh;

public sealed class SshTests : IDisposable
{
    private readonly TestPickle _t = TestPickle.Create();
    private readonly string _home = Directory.CreateTempSubdirectory("pickle-ssh").FullName;

    public SshTests()
    {
        var ssh = Directory.CreateDirectory(Path.Combine(_home, ".ssh")).FullName;
        Directory.CreateDirectory(Path.Combine(ssh, "conf.d"));
        File.WriteAllText(Path.Combine(ssh, "config"), """
            # personal
            Host web prod-web
                HostName 10.0.0.5
                User deploy
                Port 2222
            Host *.internal !bastion
                User ops
            Include conf.d/*.conf
            Match host foo
                User nobody
            Host -oProxyCommand=evil
                HostName x
            """);
        File.WriteAllText(Path.Combine(ssh, "conf.d", "work.conf"), "Host bastion\n  HostName bastion.example.com\n  User me\n");
        File.WriteAllText(Path.Combine(ssh, "known_hosts"), """
            github.com,140.82.112.3 ssh-ed25519 AAAA
            [git.example.com]:7999 ssh-rsa AAAA
            |1|hashed=|salt= ssh-ed25519 AAAA
            @cert-authority *.example.com ssh-rsa AAAA
            web ssh-ed25519 AAAA
            """);
    }

    public void Dispose() => _t.Dispose();

    [Fact]
    public void ReadsConfigWithIncludesThenKnownHostsWithoutPatternsOrOptions()
    {
        var hosts = SshHosts.Load(Path.Combine(_home, ".ssh"), _home);

        Assert.Equal(["web", "prod-web", "bastion", "github.com", "140.82.112.3", "git.example.com"], hosts.Select(h => h.Name));
        var web = hosts[0];
        Assert.Equal(("deploy@10.0.0.5:2222", SshHosts.ConfigSource), (web.Target, web.Source));
        Assert.Equal(["web"], web.Arguments);
        Assert.Equal("me@bastion.example.com", hosts.Single(h => h.Name == "bastion").Target);

        var git = hosts.Single(h => h.Name == "git.example.com");
        Assert.Equal(["-p", "7999", "git.example.com"], git.Arguments);
        Assert.Equal("ssh -p 7999 git.example.com", SshPanel.Command(git));
    }

    [Theory]
    [InlineData("web-01.example.com", true)]
    [InlineData("fe80::1%eth0", true)]
    [InlineData("-oProxyCommand=x", false)]
    [InlineData("a;b", false)]
    [InlineData("a b", false)]
    [InlineData("*.example.com", false)]
    [InlineData("", false)]
    public void OnlyPlainHostNamesAreOffered(string name, bool plain) => Assert.Equal(plain, SshHosts.IsPlainHostName(name));

    [Fact]
    public void EnterConnectsToTheFilteredHost()
    {
        var context = new PanelContext { Pickle = _t.Runtime, Argument = "bast" };
        var panel = new SshPanel(context, Path.Combine(_home, ".ssh"), _home, windowsTerminal: false);
        var script = new UiScript()
            .WaitFor("loaded", _ => panel.List.TotalCount == 6)
            .WaitFor("filtered", _ => panel.List.Selected?.Name == "bastion")
            .Press(Key.Enter);

        TuiHarness.Run(panel, script);

        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, "ssh bastion"), context.Result);
    }

    [Fact]
    public void F2OpensANewWindowsTerminalTabAndF4EditsTheCommand()
    {
        var tab = new PanelContext { Pickle = _t.Runtime, Argument = "prod" };
        var panel = new SshPanel(tab, Path.Combine(_home, ".ssh"), _home, windowsTerminal: true);
        var script = new UiScript().WaitFor("filtered", _ => panel.List.Selected?.Name == "prod-web").Press(Key.F2);
        TuiHarness.Run(panel, script);
        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, "wt -w 0 new-tab ssh prod-web"), tab.Result);

        var edit = new PanelContext { Pickle = _t.Runtime, Argument = "github" };
        var second = new SshPanel(edit, Path.Combine(_home, ".ssh"), _home, windowsTerminal: false);
        var editScript = new UiScript().WaitFor("filtered", _ => second.List.Selected?.Name == "github.com").Press(Key.F4);
        TuiHarness.Run(second, editScript);
        editScript.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.ReplaceInput, "ssh github.com "), edit.Result);
    }

    [Fact]
    public void NoSshFolderShowsAHint()
    {
        var context = new PanelContext { Pickle = _t.Runtime };
        var panel = new SshPanel(context, Path.Combine(_home, "missing"), _home, windowsTerminal: false);
        var script = new UiScript().WaitFor("hint", _ => panel.List.Status?.Contains("No hosts", StringComparison.Ordinal) == true).Press(Key.Esc);
        TuiHarness.Run(panel, script);
        script.AssertOk();
    }
}
