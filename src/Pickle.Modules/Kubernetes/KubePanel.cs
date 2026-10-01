using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui;
using Terminal.Gui.Input;

namespace Pickle.Modules.Kubernetes;

/// <summary>Kubernetes (Alt+Shift+K): pods, deployments and services of the current context; F7 switches context, F9 namespace.</summary>
internal sealed class KubePanel : ResourcePanel<KubeItem>
{
    public const string PanelId = "kubernetes";

    private readonly KubeService _kube;
    private KubeKind _kind = KubeKind.Pods;
    private string _namespace = "default";
    private string _context = string.Empty;

    public static PanelDescriptor Descriptor { get; } = new()
    {
        Id = PanelId,
        Title = "Kubernetes",
        Description = "Pods, deployments and services; switch context and namespace; logs, exec, restart, scale",
        DefaultKey = "Alt+Shift+K",
        CreateView = context => new KubePanel(context),
    };

    public KubePanel(PanelContext context)
        : base(context, "Kubernetes", i => i.Name, "Details")
    {
        _kube = new KubeService(context.Pickle.Services.Require<IProgramRunner>());
        if (Enum.TryParse<KubeKind>(context.Argument, ignoreCase: true, out var kind))
        {
            _kind = kind;
        }

        AddAction(Key.F2, "Scale", (i, ct) => Scale(i, ct), enabled: i => i.Kind == KubeKind.Deployments);
        AddAction(Key.F3, "Delete pod", (i, ct) => Act(i, "delete", null, ct), confirm: i => $"Delete pod {i.Name}? (its controller usually recreates it)", enabled: i => i.Kind == KubeKind.Pods);
        AddAction(Key.F4, "Restart", (i, ct) => Act(i, "restart", null, ct), confirm: i => $"Restart deployment {i.Name}?", enabled: i => i.Kind == KubeKind.Deployments);
        AddCommand(Key.F8, "Logs", i => _kube.ShellCommand(i, "logs"));
        AddCommand(Key.F10, "Shell", i => _kube.ShellCommand(i, "exec"));
        AddHint(Key.F6, "Pods/Deployments/Services", CycleKind);
        AddHint(Key.F7, "Context", PickContext);
        AddHint(Key.F9, "Namespace", PickNamespace);
    }

    protected override string? Category(KubeItem item) => item.Namespace;

    protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(15);

    protected override string EmptyMessage => _kube.IsAvailable ? $"No {_kind.ToString().ToLowerInvariant()} in {_namespace}." : "kubectl is not installed (or not on PATH).";

    protected override async Task<IReadOnlyList<KubeItem>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_context.Length == 0)
        {
            _context = await _kube.CurrentContextAsync(cancellationToken).ConfigureAwait(false);
            _namespace = await _kube.CurrentNamespaceAsync(cancellationToken).ConfigureAwait(false);
            OnUi(Retitle);
        }

        return await _kube.ListAsync(_kind, _namespace, cancellationToken).ConfigureAwait(false);
    }

    protected override Task<IReadOnlyList<string>> DescribeAsync(KubeItem item, CancellationToken cancellationToken) => _kube.DescribeAsync(item, cancellationToken);

    protected override string KeyOf(KubeItem item) => $"{item.Kind}|{item.Namespace}|{item.Name}";

    protected override string? Hint(KubeItem item) => item.State;

    protected override string? Detail(KubeItem item) => item.Summary;

    protected override Terminal.Gui.Drawing.Color? ItemColor(KubeItem item) =>
        item.IsHealthy ? null : item.State is "Pending" or "degraded" ? Schemes.Warning.Foreground : Schemes.ErrorText.Foreground;

    private async Task<ActionOutcome> Act(KubeItem item, string verb, string? argument, CancellationToken cancellationToken)
    {
        var result = await _kube.ActAsync(item, verb, argument, cancellationToken).ConfigureAwait(false);
        return result.Success ? ActionOutcome.Done(result.Message) : ActionOutcome.Fail(result.Message);
    }

    private async Task<ActionOutcome> Scale(KubeItem item, CancellationToken cancellationToken)
    {
        var replicas = OnUiBlocking(() => Prompt("Scale " + item.Name, "replicas"));
        return replicas is null ? ActionOutcome.Done("Cancelled.") : await Act(item, "scale", replicas, cancellationToken).ConfigureAwait(false);
    }

    private T OnUiBlocking<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() => done.SetResult(work()));
        return done.Task.GetAwaiter().GetResult();
    }

    private void CycleKind()
    {
        _kind = (KubeKind)(((int)_kind + 1) % Enum.GetValues<KubeKind>().Length);
        Retitle();
        Reload();
    }

    private void PickContext() => RunInBackground(
        ct => _kube.ContextsAsync(ct),
        contexts =>
        {
            if (Pick("Kubernetes context", contexts, c => c) is { } chosen)
            {
                RunInBackground(
                    async ct =>
                    {
                        var result = await _kube.UseContextAsync(chosen, ct).ConfigureAwait(false);
                        _context = string.Empty;
                        return result;
                    },
                    result =>
                    {
                        Details.ShowMessage("Context", result.Message);
                        Reload();
                    },
                    "switching…");
            }
        },
        "contexts…");

    private void PickNamespace() => RunInBackground(
        ct => _kube.NamespacesAsync(ct),
        namespaces =>
        {
            if (Pick("Namespace", namespaces, n => n) is { } chosen)
            {
                _namespace = chosen;
                Retitle();
                RunInBackground(ct => _kube.UseNamespaceAsync(chosen, ct), _ => Reload(), "switching…");
            }
        },
        "namespaces…");

    private void Retitle() => PanelTitle = $"Kubernetes · {(_context.Length == 0 ? "…" : _context)} · {_namespace} · {_kind.ToString().ToLowerInvariant()}";
}
