using System.Text.Json;
using System.Text.RegularExpressions;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Modules.Kubernetes;

public enum KubeKind
{
    Pods,
    Deployments,
    Services,
}

public sealed record KubeItem(KubeKind Kind, string Namespace, string Name, string Summary, string State, IReadOnlyList<string> Lines)
{
    public bool IsHealthy => State is "Running" or "Succeeded" or "ok" or "service";
}

/// <summary>kubectl through argument lists. Names are checked against Kubernetes' own naming rules before they reach any command.</summary>
public sealed partial class KubeService(IProgramRunner runner)
{
    public const string AllNamespaces = "all";

    public bool IsAvailable => runner.Find("kubectl") is not null;

    public async Task<string> CurrentContextAsync(CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("kubectl", ["config", "current-context"], null, cancellationToken).ConfigureAwait(false);
        return result.Success ? result.StdOut.Trim() : string.Empty;
    }

    public async Task<string> CurrentNamespaceAsync(CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("kubectl", ["config", "view", "--minify", "-o", "jsonpath={..namespace}"], null, cancellationToken).ConfigureAwait(false);
        return result.Success && result.StdOut.Trim() is { Length: > 0 } ns ? ns.Trim() : "default";
    }

    public async Task<IReadOnlyList<string>> ContextsAsync(CancellationToken cancellationToken) =>
        Lines(await Output(["config", "get-contexts", "-o", "name"], cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<string>> NamespacesAsync(CancellationToken cancellationToken) =>
        [AllNamespaces, .. ParseNames(await Output(["get", "namespaces", "-o", "json"], cancellationToken).ConfigureAwait(false))];

    public async Task<IReadOnlyList<KubeItem>> ListAsync(KubeKind kind, string ns, CancellationToken cancellationToken)
    {
        var resource = kind switch { KubeKind.Pods => "pods", KubeKind.Deployments => "deployments", _ => "services" };
        var json = await Output(["get", resource, .. NamespaceArguments(ns), "-o", "json"], cancellationToken).ConfigureAwait(false);
        return kind switch
        {
            KubeKind.Pods => ParsePods(json),
            KubeKind.Deployments => ParseDeployments(json),
            _ => ParseServices(json),
        };
    }

    public async Task<IReadOnlyList<string>> DescribeAsync(KubeItem item, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("kubectl", ["describe", Resource(item.Kind), item.Name, "-n", Checked(item.Namespace)], new ProgramRunOptions { Timeout = TimeSpan.FromSeconds(20) }, cancellationToken).ConfigureAwait(false);
        var lines = (result.StdOut + result.StdErr).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).ToList();
        return lines.Count > 1 ? [.. lines.Take(120)] : item.Lines;
    }

    public async Task<ServiceOperationResult> ActAsync(KubeItem item, string verb, string? argument, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("kubectl", Arguments(item, verb, argument), new ProgramRunOptions { Timeout = TimeSpan.FromMinutes(2) }, cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(result.Success, result.Success ? result.StdOut.Trim().Split('\n')[^1] : result.Message);
    }

    public IReadOnlyList<string> Arguments(KubeItem item, string verb, string? argument)
    {
        Checked(item.Name);
        var ns = Checked(item.Namespace);
        return (item.Kind, verb) switch
        {
            (KubeKind.Pods, "delete") => ["delete", "pod", item.Name, "-n", ns],
            (KubeKind.Deployments, "restart") => ["rollout", "restart", "deployment/" + item.Name, "-n", ns],
            (KubeKind.Deployments, "scale") when int.TryParse(argument, out var replicas) && replicas is >= 0 and <= 1000 =>
                ["scale", "deployment/" + item.Name, "--replicas=" + replicas.ToString(System.Globalization.CultureInfo.InvariantCulture), "-n", ns],
            (KubeKind.Deployments, "scale") => throw new ArgumentException("Replicas must be a number from 0 to 1000."),
            _ => throw new ArgumentException($"'{verb}' is not something to do with {item.Kind.ToString().ToLowerInvariant()}."),
        };
    }

    public string? ShellCommand(KubeItem item, string what)
    {
        Checked(item.Name);
        var ns = Checked(item.Namespace);
        return (item.Kind, what) switch
        {
            (KubeKind.Pods, "logs") => PowerShellQuote.Command("kubectl", "logs", "-f", "--tail=200", "-n", ns, item.Name),
            (KubeKind.Pods, "exec") => PowerShellQuote.Command("kubectl", "exec", "-it", "-n", ns, item.Name, "--", "sh"),
            (KubeKind.Deployments, "logs") => PowerShellQuote.Command("kubectl", "logs", "-f", "--tail=200", "-n", ns, "deployment/" + item.Name),
            _ => null,
        };
    }

    public async Task<ServiceOperationResult> UseContextAsync(string context, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("kubectl", ["config", "use-context", CheckedContext(context)], null, cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(result.Success, result.Success ? $"Context is now {context}." : result.Message);
    }

    public async Task<ServiceOperationResult> UseNamespaceAsync(string ns, CancellationToken cancellationToken)
    {
        if (ns == AllNamespaces)
        {
            return new ServiceOperationResult(true, "Showing all namespaces.");
        }

        var result = await runner.RunAsync("kubectl", ["config", "set-context", "--current", "--namespace=" + Checked(ns)], null, cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(result.Success, result.Success ? $"Namespace is now {ns}." : result.Message);
    }

    public static IReadOnlyList<string> NamespaceArguments(string ns) => ns == AllNamespaces ? ["--all-namespaces"] : ["-n", Checked(ns)];

    public static IReadOnlyList<string> ParseNames(string json) =>
        ModuleJson.Document(json) is { } root ? [.. root.Items("items").Select(i => i.Child("metadata")?.Text("name") ?? string.Empty).Where(n => n.Length > 0).Order(StringComparer.Ordinal)] : [];

    public static IReadOnlyList<KubeItem> ParsePods(string json)
    {
        var pods = new List<KubeItem>();
        if (ModuleJson.Document(json) is not { } root)
        {
            return pods;
        }

        foreach (var pod in root.Items("items"))
        {
            var meta = pod.Child("metadata") ?? default;
            var status = pod.Child("status") ?? default;
            var containers = status.Items("containerStatuses").ToList();
            var ready = containers.Count(c => c.Child("ready") is { ValueKind: JsonValueKind.True });
            var restarts = containers.Sum(c => c.Child("restartCount") is { ValueKind: JsonValueKind.Number } n ? n.GetInt32() : 0);
            var phase = status.Text("phase", "Unknown");
            var waiting = containers.Select(c => c.Child("state")?.Child("waiting")?.Text("reason")).FirstOrDefault(r => !string.IsNullOrEmpty(r));
            var state = waiting ?? phase;
            var node = (pod.Child("spec") ?? default).Text("nodeName", "—");
            var name = meta.Text("name");
            var ns = meta.Text("namespace", "default");
            pods.Add(new KubeItem(KubeKind.Pods, ns, name, $"{ready}/{Math.Max(containers.Count, 1)} ready · {restarts} restarts · {node}", state,
            [
                $"Pod:       {name}",
                $"Namespace: {ns}",
                $"State:     {state}",
                $"Ready:     {ready}/{containers.Count}",
                $"Restarts:  {restarts}",
                $"Node:      {node}",
                $"Pod IP:    {status.Text("podIP", "—")}",
                $"Started:   {meta.Text("creationTimestamp")}",
            ]));
        }

        return [.. pods.Where(p => p.Name.Length > 0).OrderBy(p => p.Namespace, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal)];
    }

    public static IReadOnlyList<KubeItem> ParseDeployments(string json)
    {
        var list = new List<KubeItem>();
        if (ModuleJson.Document(json) is not { } root)
        {
            return list;
        }

        foreach (var deployment in root.Items("items"))
        {
            var meta = deployment.Child("metadata") ?? default;
            var spec = deployment.Child("spec") ?? default;
            var status = deployment.Child("status") ?? default;
            int Number(JsonElement element, string name) => element.Child(name) is { ValueKind: JsonValueKind.Number } n ? n.GetInt32() : 0;
            var (desired, ready) = (Number(spec, "replicas"), Number(status, "readyReplicas"));
            var name = meta.Text("name");
            var ns = meta.Text("namespace", "default");
            list.Add(new KubeItem(KubeKind.Deployments, ns, name, $"{ready}/{desired} ready", ready == desired ? "ok" : "degraded",
            [
                $"Deployment: {name}",
                $"Namespace:  {ns}",
                $"Replicas:   {ready} ready of {desired} desired",
                $"Updated:    {Number(status, "updatedReplicas")}",
                $"Available:  {Number(status, "availableReplicas")}",
            ]));
        }

        return [.. list.Where(d => d.Name.Length > 0).OrderBy(d => d.Namespace, StringComparer.Ordinal).ThenBy(d => d.Name, StringComparer.Ordinal)];
    }

    public static IReadOnlyList<KubeItem> ParseServices(string json)
    {
        var list = new List<KubeItem>();
        if (ModuleJson.Document(json) is not { } root)
        {
            return list;
        }

        foreach (var service in root.Items("items"))
        {
            var meta = service.Child("metadata") ?? default;
            var spec = service.Child("spec") ?? default;
            var ports = string.Join(", ", spec.Items("ports").Select(p => $"{p.Text("port")}/{p.Text("protocol", "TCP")}"));
            var name = meta.Text("name");
            var ns = meta.Text("namespace", "default");
            list.Add(new KubeItem(KubeKind.Services, ns, name, $"{spec.Text("type", "ClusterIP")} · {spec.Text("clusterIP", "—")} · {ports}", "service",
            [
                $"Service:    {name}",
                $"Namespace:  {ns}",
                $"Type:       {spec.Text("type", "ClusterIP")}",
                $"Cluster IP: {spec.Text("clusterIP", "—")}",
                $"Ports:      {ports}",
            ]));
        }

        return [.. list.Where(s => s.Name.Length > 0).OrderBy(s => s.Namespace, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal)];
    }

    internal static string Checked(string name) =>
        DnsName().IsMatch(name) ? name : throw new ArgumentException($"'{name}' is not a Kubernetes name (lowercase letters, digits, - and .).");

    internal static string CheckedContext(string name) =>
        ContextName().IsMatch(name) ? name : throw new ArgumentException($"'{name}' is not a kubectl context name.");

    private static string Resource(KubeKind kind) => kind switch { KubeKind.Pods => "pod", KubeKind.Deployments => "deployment", _ => "service" };

    private static IReadOnlyList<string> Lines(string output) => [.. output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => ContextName().IsMatch(l))];

    private async Task<string> Output(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("kubectl", arguments, new ProgramRunOptions { Timeout = TimeSpan.FromSeconds(30) }, cancellationToken).ConfigureAwait(false);
        if (!result.WasFound)
        {
            throw new InvalidOperationException("kubectl was not found on PATH.");
        }

        return result.Success ? result.StdOut : throw new InvalidOperationException(result.Message);
    }

    [GeneratedRegex(@"^[a-z0-9]([-a-z0-9.]{0,251}[a-z0-9])?$")]
    private static partial Regex DnsName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.:@/-]{0,253}$")]
    private static partial Regex ContextName();
}
