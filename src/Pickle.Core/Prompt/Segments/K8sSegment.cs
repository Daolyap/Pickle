using Pickle.Abstractions;

namespace Pickle.Core.Prompt.Segments;

public sealed record KubeContext(string Context, string? Namespace);

/// <summary>
/// Kubernetes current-context from $KUBECONFIG or ~/.kube/config. Option <c>namespace</c> (default true) appends
/// ":namespace" when the context sets a non-default one.
/// </summary>
public sealed class K8sSegment(SegmentEnvironment environment) : IPromptSegment
{
    public string Type => "k8s";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken)
    {
        if (environment.GetKubeContext() is not { } kube)
        {
            return SegmentText.Hide();
        }

        var text = SegmentText.Sanitize(kube.Context);
        if (style.OptionBool("namespace", true) && kube.Namespace is { Length: > 0 } ns && ns != "default")
        {
            text += ":" + SegmentText.Sanitize(ns);
        }

        return SegmentText.Show(text);
    }
}

/// <summary>Reads kubeconfig files, re-parsing a file only when its timestamp or size changes.</summary>
public sealed class KubeConfigReader(Func<string, string?> getEnv, Func<string?> home)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime Modified, long Length, KubeConfigInfo Info)> _cache = new(StringComparer.Ordinal);

    public KubeContext? Read()
    {
        var infos = ConfigFiles().Select(Load).OfType<KubeConfigInfo>().ToList();

        // kubectl merges the files: the first one that sets a value wins.
        var current = infos.Select(i => i.CurrentContext).FirstOrDefault(c => !string.IsNullOrEmpty(c));
        if (current is null)
        {
            return null;
        }

        string? ns = null;
        foreach (var info in infos)
        {
            if (info.Namespaces.TryGetValue(current, out var found))
            {
                ns = found;
                break;
            }
        }

        return new KubeContext(current, ns);
    }

    private IEnumerable<string> ConfigFiles()
    {
        var env = getEnv("KUBECONFIG");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return home() is { } h ? [Path.Combine(h, ".kube", "config")] : [];
    }

    private KubeConfigInfo? Load(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return null;
            }

            lock (_gate)
            {
                if (_cache.TryGetValue(path, out var cached) && cached.Modified == file.LastWriteTimeUtc && cached.Length == file.Length)
                {
                    return cached.Info;
                }
            }

            var info = KubeConfigInfo.Parse(File.ReadAllText(path));
            lock (_gate)
            {
                _cache[path] = (file.LastWriteTimeUtc, file.Length, info);
            }

            return info;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>The two things the prompt needs from a kubeconfig, parsed from the small YAML subset kubectl writes.</summary>
public sealed record KubeConfigInfo(string? CurrentContext, IReadOnlyDictionary<string, string> Namespaces)
{
    public static KubeConfigInfo Parse(string yaml)
    {
        string? current = null;
        var namespaces = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = string.Empty;
        var itemIndent = -1;
        string? itemName = null;
        string? itemNamespace = null;

        void FlushItem()
        {
            if (itemName is not null && itemNamespace is not null)
            {
                namespaces.TryAdd(itemName, itemNamespace);
            }

            itemName = null;
            itemNamespace = null;
        }

        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed == "---")
            {
                continue;
            }

            var indent = line.Length - trimmed.Length;
            if (indent == 0 && trimmed[0] != '-')
            {
                FlushItem();
                var (key, value) = SplitKey(trimmed);
                section = key;
                if (key == "current-context")
                {
                    current = value;
                }

                continue;
            }

            if (section != "contexts")
            {
                continue;
            }

            var keyIndent = indent;
            if (trimmed.StartsWith('-'))
            {
                FlushItem();
                var afterDash = trimmed[1..].TrimStart();
                keyIndent = indent + (trimmed.Length - afterDash.Length);
                itemIndent = keyIndent;
                trimmed = afterDash;
                if (trimmed.Length == 0)
                {
                    continue;
                }
            }

            var (k, v) = SplitKey(trimmed);
            if (k == "name" && keyIndent == itemIndent)
            {
                itemName = v;
            }
            else if (k == "namespace" && keyIndent > itemIndent)
            {
                itemNamespace = v;
            }
        }

        FlushItem();
        return new KubeConfigInfo(string.IsNullOrEmpty(current) ? null : current, namespaces);
    }

    private static (string Key, string? Value) SplitKey(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return (text.Trim(), null);
        }

        var value = text[(colon + 1)..].Trim();
        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        if (comment >= 0 && value[0] is not ('"' or '\''))
        {
            value = value[..comment].TrimEnd();
        }

        if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
        {
            value = value[1..^1];
        }

        return (text[..colon].Trim(), value.Length == 0 ? null : value);
    }
}
