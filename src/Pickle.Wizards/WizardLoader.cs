using System.Text.Json;
using System.Text.Json.Nodes;
using Pickle.Abstractions;

namespace Pickle.Wizards;

/// <summary>Reads wizard JSON (embedded Definitions/*.json and the user's wizards folder).</summary>
public static class WizardLoader
{
    private const string ResourcePrefix = "Pickle.Wizards.Definitions.";

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <exception cref="JsonException">Invalid JSON or schema.</exception>
    public static WizardDefinition Parse(string json)
    {
        var node = JsonNode.Parse(json, NodeOptions, DocumentOptions) as JsonObject
            ?? throw new JsonException("A wizard definition must be a JSON object.");
        return node.Deserialize<WizardDefinition>(PickleJson.Options)
            ?? throw new JsonException("Empty wizard definition.");
    }

    public static IReadOnlyList<string> EmbeddedNames =>
        [.. typeof(WizardLoader).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)];

    public static WizardDefinition LoadEmbedded(string resourceName)
    {
        using var stream = typeof(WizardLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException(resourceName);
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>All built-in definitions; a broken one is reported and skipped.</summary>
    public static IReadOnlyList<WizardDefinition> LoadEmbedded(Action<string, Exception>? onError = null)
    {
        var list = new List<WizardDefinition>();
        foreach (var name in EmbeddedNames)
        {
            try
            {
                list.Add(LoadEmbedded(name));
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
            {
                onError?.Invoke(name, ex);
            }
        }

        return list;
    }

    /// <summary>Every *.json in <paramref name="directory"/> (sorted by name); unreadable files are reported and skipped.</summary>
    public static IReadOnlyList<(string Path, WizardDefinition Definition)> LoadDirectory(string directory, Action<string, Exception>? onError = null)
    {
        var list = new List<(string, WizardDefinition)>();
        if (!Directory.Exists(directory))
        {
            return list;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                list.Add((path, Parse(File.ReadAllText(path))));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                onError?.Invoke(path, ex);
            }
        }

        return list;
    }
}
