using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui;

namespace Pickle.Modules.Kubernetes;

/// <summary><c>pk k8s</c>: the panel interactively; objects and a few one-line actions for scripts.</summary>
internal sealed class KubeCommand : PanelCommand
{
    public override string Name => "k8s";

    public override string Description => "Kubernetes pods, deployments and services; contexts and namespaces";

    public override string Usage => "pk k8s [pods|deployments|services] | list [kind] [--namespace ns|all] | contexts | use <context> | namespace <ns> | restart <deployment> [--namespace ns] [--yes]";

    public override IReadOnlyList<string> Examples => ["pk k8s", "pk k8s list pods --namespace all", "pk k8s restart api --namespace prod"];

    protected override string PanelId => KubePanel.PanelId;

    protected override string? PanelArgument(IReadOnlyList<string> args) => args.Count > 0 ? args[0] : null;

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var verb = raw.Count > 0 ? raw[0].ToLowerInvariant() : string.Empty;
        var kube = new KubeService(output.Pickle.Services.Require<IProgramRunner>());
        var args = CommandArgs.Parse(raw.Skip(1).ToList(), "namespace");
        switch (verb)
        {
            case "contexts":
                var current = await kube.CurrentContextAsync(cancellationToken).ConfigureAwait(false);
                foreach (var context in await kube.ContextsAsync(cancellationToken).ConfigureAwait(false))
                {
                    output.Object(new { Context = context, Current = context == current });
                }

                return 0;
            case "use" when args.Arg(0) is { } context:
                return Report(output, await kube.UseContextAsync(context, cancellationToken).ConfigureAwait(false));
            case "namespace" or "ns" when args.Arg(0) is { } ns:
                return Report(output, await kube.UseNamespaceAsync(ns, cancellationToken).ConfigureAwait(false));
            case "restart" when args.Arg(0) is { } deployment:
                if (!output.Confirm(args, $"Restart deployment {deployment}?", false))
                {
                    output.Muted("Cancelled.");
                    return 1;
                }

                var item = new KubeItem(KubeKind.Deployments, args.Value("namespace") ?? await kube.CurrentNamespaceAsync(cancellationToken).ConfigureAwait(false), deployment, string.Empty, "ok", []);
                return Report(output, await kube.ActAsync(item, "restart", null, cancellationToken).ConfigureAwait(false));
            default:
                return await base.RunAsync(output, raw, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override async Task<int> ListAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw, "namespace");
        var kind = Enum.TryParse<KubeKind>(args.Arg(0), ignoreCase: true, out var parsed) ? parsed : KubeKind.Pods;
        var kube = new KubeService(output.Pickle.Services.Require<IProgramRunner>());
        var ns = args.Value("namespace") ?? await kube.CurrentNamespaceAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in await kube.ListAsync(kind, ns, cancellationToken).ConfigureAwait(false))
        {
            output.Object(Display.Columns(item, "Namespace", "Name", "State", "Summary"));
        }

        return 0;
    }

    private static int Report(CommandOutput output, ServiceOperationResult result)
    {
        if (result.Success)
        {
            output.Success(result.Message);
            return 0;
        }

        output.Failure(result.Message);
        return 1;
    }
}
