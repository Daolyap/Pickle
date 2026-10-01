using Pickle.Abstractions;
using Pickle.Modules.Kubernetes;
using Pickle.Testing.Fakes;
using Pickle.Tui.Tests;
using Terminal.Gui.Input;

namespace Pickle.Modules.Tests.Kubernetes;

public class KubernetesTests
{
    private const string Pods = """
        {"items":[
          {"metadata":{"name":"api-7d9f8-abcde","namespace":"prod","creationTimestamp":"2025-10-01T10:00:00Z"},"spec":{"nodeName":"node-1"},"status":{"phase":"Running","podIP":"10.1.0.4","containerStatuses":[{"ready":true,"restartCount":0,"state":{"running":{}}}]}},
          {"metadata":{"name":"worker-0","namespace":"prod"},"spec":{"nodeName":"node-2"},"status":{"phase":"Running","containerStatuses":[{"ready":false,"restartCount":7,"state":{"waiting":{"reason":"CrashLoopBackOff"}}},{"ready":true,"restartCount":0}]}}
        ]}
        """;

    [Fact]
    public void ParsesPodsWithReadinessRestartsAndCrashLoops()
    {
        var pods = KubeService.ParsePods(Pods);

        Assert.Equal(["api-7d9f8-abcde", "worker-0"], pods.Select(p => p.Name));
        Assert.Equal(["Running", "CrashLoopBackOff"], pods.Select(p => p.State));
        Assert.Equal("1/1 ready · 0 restarts · node-1", pods[0].Summary);
        Assert.Equal("1/2 ready · 7 restarts · node-2", pods[1].Summary);
        Assert.False(pods[1].IsHealthy);
    }

    [Fact]
    public void ParsesDeploymentsServicesAndNamespaces()
    {
        var deployments = KubeService.ParseDeployments("""{"items":[{"metadata":{"name":"api","namespace":"prod"},"spec":{"replicas":3},"status":{"readyReplicas":2,"updatedReplicas":3,"availableReplicas":2}}]}""");
        var services = KubeService.ParseServices("""{"items":[{"metadata":{"name":"api","namespace":"prod"},"spec":{"type":"ClusterIP","clusterIP":"10.96.0.5","ports":[{"port":80,"protocol":"TCP"}]}}]}""");

        Assert.Equal(("2/3 ready", "degraded"), (deployments[0].Summary, deployments[0].State));
        Assert.Equal("ClusterIP · 10.96.0.5 · 80/TCP", services[0].Summary);
        Assert.Equal(["default", "kube-system", "prod"], KubeService.ParseNames("""{"items":[{"metadata":{"name":"prod"}},{"metadata":{"name":"default"}},{"metadata":{"name":"kube-system"}}]}"""));
    }

    [Fact]
    public void CommandsCarryNamesAsSeparateArgumentsAndRejectBadOnes()
    {
        var kube = new KubeService(new FakeProgramRunner());
        var deployment = new KubeItem(KubeKind.Deployments, "prod", "api", string.Empty, "ok", []);
        var pod = new KubeItem(KubeKind.Pods, "prod", "api-1", string.Empty, "Running", []);

        Assert.Equal(["rollout", "restart", "deployment/api", "-n", "prod"], kube.Arguments(deployment, "restart", null));
        Assert.Equal(["scale", "deployment/api", "--replicas=3", "-n", "prod"], kube.Arguments(deployment, "scale", "3"));
        Assert.Throws<ArgumentException>(() => kube.Arguments(deployment, "scale", "-1"));
        Assert.Throws<ArgumentException>(() => kube.Arguments(deployment, "scale", "lots"));
        Assert.Throws<ArgumentException>(() => kube.Arguments(pod with { Name = "x; rm" }, "delete", null));
        Assert.Throws<ArgumentException>(() => kube.Arguments(pod with { Namespace = "--all-namespaces" }, "delete", null));
        Assert.Equal("kubectl logs -f --tail=200 -n prod api-1", kube.ShellCommand(pod, "logs"));
        Assert.Equal("kubectl exec -it -n prod api-1 -- sh", kube.ShellCommand(pod, "exec"));
        Assert.Equal(["--all-namespaces"], KubeService.NamespaceArguments("all"));
    }

    [Fact]
    public void ThePanelListsPodsDeletesAfterConfirmAndOpensLogs()
    {
        var runner = new FakeProgramRunner()
            .On("kubectl", "config current-context", "prod-cluster\n")
            .On("kubectl", "config view", "prod")
            .On("kubectl", "get pods", Pods)
            .On("kubectl", "describe", "Name: worker-0\nStatus: Running\n")
            .On("kubectl", "delete", "pod \"worker-0\" deleted\n");
        var script = new UiScript()
            .WaitFor("loaded", app => TuiHarness.Top<KubePanel>(app).List.TotalCount == 2)
            .Do("select worker", app => TuiHarness.Top<KubePanel>(app).List.Select(TuiHarness.Top<KubePanel>(app).List.VisibleItems[1]))
            .WaitFor("described", app => TuiHarness.Top<KubePanel>(app).Details.PlainText.Contains("Name: worker-0", StringComparison.Ordinal))
            .Press(Key.F3)
            .WaitFor("dialog", app => app.TopRunnableView is Terminal.Gui.Views.Dialog)
            .Press(Key.Y)
            .WaitFor("deleted", _ => runner.CommandLines("kubectl").Any(c => c == "delete pod worker-0 -n prod"))
            .Press(Key.F8);
        var (t, host) = ModuleTestSupport.StartPanels("kubernetes", runner, script);
        using var _ = t;

        var result = host.Show("kubernetes");

        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, "kubectl logs -f --tail=200 -n prod worker-0"), result);
    }

    [Fact]
    public void ThePkCommandListsContextsAndRestartsDeployments()
    {
        var runner = new FakeProgramRunner()
            .On("kubectl", "config current-context", "a\n")
            .On("kubectl", "config get-contexts", "a\nb\n")
            .On("kubectl", "rollout", "deployment.apps/api restarted\n");
        using var t = ModuleTestSupport.Start("kubernetes", runner);

        Assert.Equal(["a:True", "b:False"], t.Run("pk k8s contexts | ForEach-Object { \"$($_.Context):$($_.Current)\" }"));
        t.Run("pk k8s restart api --namespace prod --yes");

        Assert.Contains("rollout restart deployment/api -n prod", runner.CommandLines("kubectl"));
    }
}
