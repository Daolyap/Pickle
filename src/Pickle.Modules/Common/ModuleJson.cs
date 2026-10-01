using System.Text.Json;

namespace Pickle.Modules;

/// <summary>Small helpers for the JSON that CLIs print (<c>docker ps --format '{{json .}}'</c>, <c>kubectl -o json</c>, <c>gh --json</c>).</summary>
internal static class ModuleJson
{
    /// <summary>One JSON value per line; lines that are not JSON are skipped.</summary>
    public static IReadOnlyList<JsonElement> Lines(string output)
    {
        var list = new List<JsonElement>();
        foreach (var line in output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                list.Add(document.RootElement.Clone());
            }
            catch (JsonException)
            {
            }
        }

        return list;
    }

    /// <summary>A whole JSON document, or null when it does not parse.</summary>
    public static JsonElement? Document(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Text(this JsonElement element, string name, string fallback = "") =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.String => value.GetString() ?? fallback, JsonValueKind.Null => fallback, _ => value.ToString() }
            : fallback;

    public static JsonElement? Child(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    public static IEnumerable<JsonElement> Items(this JsonElement element, string name) =>
        element.Child(name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
}
