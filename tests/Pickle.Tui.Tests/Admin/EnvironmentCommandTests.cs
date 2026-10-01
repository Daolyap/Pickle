using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.Admin;

namespace Pickle.Tui.Tests.Admin;

public class EnvironmentCommandTests
{
    private static (TestPickle T, FakeHostsService Hosts, FakeEnvironmentStore Env) Start()
    {
        var t = TestPickle.Create(start: true, plugins: [new AdminPanelsPlugin()]);
        var hosts = new FakeHostsService { Text = "127.0.0.1 localhost\n10.0.0.5 web\n" };
        var env = new FakeEnvironmentStore();
        t.Runtime.Services.Add<IHostsService>(hosts);
        t.Runtime.Services.Add<IEnvironmentStore>(env);
        return (t, hosts, env);
    }

    [Fact]
    public void HostsAddRemoveDisableGoThroughOneWrite()
    {
        var (t, hosts, _) = Start();
        using var _ = t;

        t.Run("pk hosts add 10.0.0.9 nas nas.lan --comment 'file server' --yes");
        t.Run("pk hosts disable web --yes");
        t.Run("pk hosts remove localhost --yes");

        Assert.Equal(
            [
                "127.0.0.1 localhost\n10.0.0.5 web\n10.0.0.9        nas nas.lan  # file server\n",
                "127.0.0.1 localhost\n# 10.0.0.5        web\n10.0.0.9        nas nas.lan  # file server\n",
                "# 10.0.0.5        web\n10.0.0.9        nas nas.lan  # file server\n",
            ],
            hosts.Written);
        Assert.Equal(["10.0.0.9", "10.0.0.5"], t.Run("pk hosts list | ForEach-Object Address").Order(StringComparer.Ordinal).Reverse());
    }

    [Fact]
    public void HostsRejectsBadInputBeforeWriting()
    {
        var (t, hosts, _) = Start();
        using var _ = t;

        Assert.Throws<InvalidOperationException>(() => t.Run("pk hosts add not-an-ip nas --yes"));
        Assert.Throws<InvalidOperationException>(() => t.Run("pk hosts add 10.0.0.9 'bad name' --yes"));

        Assert.Empty(hosts.Written);
    }

    [Fact]
    public void EnvSetGetUnsetAndPathEditInTheChosenScope()
    {
        var (t, _, env) = Start();
        using var _ = t;

        t.Run("pk env set EDITOR vim --scope pickle");
        t.Run("pk path add /opt/bin --scope pickle --first");
        t.Run("pk path add /usr/local/bin --scope pickle");
        t.Run("pk path remove /opt/bin --scope pickle");

        Assert.Equal(["vim"], t.Run("pk env get EDITOR --scope pickle"));
        Assert.Equal(["/usr/local/bin"], env.Path(EnvironmentScope.Pickle));
        Assert.Equal(["set Pickle EDITOR=vim", "path Pickle /opt/bin", "path Pickle /opt/bin|/usr/local/bin", "path Pickle /usr/local/bin"], env.Calls);
        Assert.Equal(["1:/usr/local/bin"], t.Run("pk path list --scope pickle | ForEach-Object { \"$($_.Position):$($_.Path)\" }"));
    }

    [Fact]
    public void MachineScopeAsksBeforeTheElevatedWrite()
    {
        var (t, _, env) = Start();
        using var _ = t;

        t.Terminal.Type("n").Press("Enter");
        t.Run("pk env set HTTP_PROXY http://p:3128 --scope machine");
        Assert.Empty(env.Calls);

        t.Run("pk env set HTTP_PROXY http://p:3128 --scope machine --yes");
        Assert.Equal(["set Machine HTTP_PROXY=http://p:3128"], env.Calls);
    }

    [Fact]
    public void ScopesThatDoNotExistHereAreRefused()
    {
        var (t, _, env) = Start();
        using var _ = t;
        env.Scopes = [EnvironmentScope.Pickle, EnvironmentScope.Process];

        Assert.Throws<InvalidOperationException>(() => t.Run("pk env set X 1 --scope machine"));
    }
}
