using System.Text.Json;
using System.Text.Json.Nodes;
using Pickle.Abstractions;

namespace Pickle.Core.Prompt.Segments;

/// <summary>
/// AWS profile from AWS_PROFILE (or AWS_DEFAULT_PROFILE), with the region from AWS_REGION / AWS_DEFAULT_REGION when
/// option <c>region</c> (default true). Hidden when no profile is set, like the AWS CLI's own default.
/// </summary>
public sealed class AwsSegment(SegmentEnvironment environment) : IPromptSegment
{
    public string Type => "aws";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken)
    {
        var env = environment.GetEnvironmentVariable;
        return Format(env("AWS_PROFILE") ?? env("AWS_DEFAULT_PROFILE"), env("AWS_REGION") ?? env("AWS_DEFAULT_REGION"), style.OptionBool("region", true)) is { } text
            ? SegmentText.Show(text)
            : SegmentText.Hide();
    }

    public static string? Format(string? profile, string? region, bool showRegion) =>
        string.IsNullOrWhiteSpace(profile)
            ? null
            : SegmentText.Sanitize(profile.Trim()) + (showRegion && !string.IsNullOrWhiteSpace(region) ? $" ({SegmentText.Sanitize(region.Trim())})" : string.Empty);
}

/// <summary>The default Azure subscription's name from azureProfile.json (az login / az account set).</summary>
public sealed class AzureSegment(SegmentEnvironment environment) : IPromptSegment
{
    public string Type => "azure";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken) =>
        environment.GetAzureSubscription() is { Length: > 0 } name ? SegmentText.Show(SegmentText.Sanitize(name)) : SegmentText.Hide();

    /// <summary>The <c>isDefault</c> subscription's name in an azureProfile.json (which the CLI writes with a BOM).</summary>
    public static string? ParseProfile(string json)
    {
        try
        {
            var root = JsonNode.Parse(json.TrimStart('﻿'));
            return root?["subscriptions"] is JsonArray subscriptions
                ? subscriptions.OfType<JsonObject>()
                    .FirstOrDefault(s => s["isDefault"] is JsonValue d && d.TryGetValue<bool>(out var isDefault) && isDefault)?["name"]?.GetValue<string>()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>The active gcloud configuration's project (<c>gcloud config set project</c>).</summary>
public sealed class GcloudSegment(SegmentEnvironment environment) : IPromptSegment
{
    public string Type => "gcloud";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken) =>
        environment.GetGcloudProject() is { Length: > 0 } project ? SegmentText.Show(SegmentText.Sanitize(project)) : SegmentText.Hide();

    /// <summary><c>project</c> in the <c>[core]</c> section of a gcloud configuration file (INI).</summary>
    public static string? ParseProject(string ini)
    {
        var section = string.Empty;
        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
            }
            else if (section.Equals("core", StringComparison.OrdinalIgnoreCase) && line.Split('=', 2) is [var key, var value]
                && key.Trim().Equals("project", StringComparison.OrdinalIgnoreCase) && value.Trim().Length > 0)
            {
                return value.Trim();
            }
        }

        return null;
    }
}

/// <summary>The Docker context (DOCKER_CONTEXT, else currentContext in ~/.docker/config.json); hidden for "default".</summary>
public sealed class DockerSegment(SegmentEnvironment environment) : IPromptSegment
{
    public string Type => "docker";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken)
    {
        var name = environment.GetEnvironmentVariable("DOCKER_CONTEXT") is { Length: > 0 } env ? env : environment.GetDockerContext();
        return string.IsNullOrWhiteSpace(name) || name.Trim().Equals("default", StringComparison.OrdinalIgnoreCase)
            ? SegmentText.Hide()
            : SegmentText.Show(SegmentText.Sanitize(name.Trim()));
    }

    public static string? ParseConfig(string json)
    {
        try
        {
            return JsonNode.Parse(json.TrimStart('﻿'))?["currentContext"] is JsonValue value && value.TryGetValue<string>(out var name) ? name : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Reads the cloud CLIs' own config files, re-parsing each only when its timestamp or size changes.</summary>
public sealed class CloudConfigReader(Func<string, string?> getEnv, Func<string?> home)
{
    private readonly CachedFile<string> _azure = new(AzureSegment.ParseProfile);
    private readonly CachedFile<string> _gcloudActive = new(text => text.Trim() is { Length: > 0 } name ? name : null);
    private readonly CachedFile<string> _gcloudConfig = new(GcloudSegment.ParseProject);
    private readonly CachedFile<string> _docker = new(DockerSegment.ParseConfig);

    public string? AzureSubscription() =>
        (getEnv("AZURE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Under(".azure")) is { } folder
            ? _azure.Read(Path.Combine(folder, "azureProfile.json"))
            : null;

    public string? GcloudProject()
    {
        var folder = getEnv("CLOUDSDK_CONFIG") is { Length: > 0 } dir
            ? dir
            : OperatingSystem.IsWindows() && getEnv("APPDATA") is { Length: > 0 } appData ? Path.Combine(appData, "gcloud") : Under(Path.Combine(".config", "gcloud"));
        if (folder is null)
        {
            return null;
        }

        var active = getEnv("CLOUDSDK_ACTIVE_CONFIG_NAME") is { Length: > 0 } named ? named : _gcloudActive.Read(Path.Combine(folder, "active_config")) ?? "default";
        return active.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : _gcloudConfig.Read(Path.Combine(folder, "configurations", "config_" + active));
    }

    public string? DockerContext() =>
        (getEnv("DOCKER_CONFIG") is { Length: > 0 } dir ? dir : Under(".docker")) is { } folder
            ? _docker.Read(Path.Combine(folder, "config.json"))
            : null;

    private string? Under(string relative) => home() is { } h ? Path.Combine(h, relative) : null;
}

/// <summary>A small file parsed on first read and again only after it changes (timestamp or size).</summary>
public sealed class CachedFile<T>(Func<string, T?> parse)
    where T : class
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTime Modified, long Length, T? Value)> _cache = new(StringComparer.Ordinal);

    public T? Read(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 1 << 20)
            {
                return null;
            }

            lock (_gate)
            {
                if (_cache.TryGetValue(path, out var cached) && cached.Modified == file.LastWriteTimeUtc && cached.Length == file.Length)
                {
                    return cached.Value;
                }
            }

            var value = parse(File.ReadAllText(path));
            lock (_gate)
            {
                _cache[path] = (file.LastWriteTimeUtc, file.Length, value);
            }

            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
