using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pickle.Abstractions;

namespace Pickle.Core.Commands;

/// <summary>Conversions between PowerShell objects (hashtables, PSCustomObject, arrays) and JSON nodes.</summary>
public static class PsJson
{
    public static JsonNode? ToNode(object? value, int depth = 0)
    {
        if (depth > 32)
        {
            return JsonValue.Create(value?.ToString());
        }

        if (value is PSObject pso)
        {
            if (pso.BaseObject is PSCustomObject)
            {
                var obj = new JsonObject();
                foreach (var property in pso.Properties)
                {
                    obj[property.Name] = ToNode(SafeValue(property), depth + 1);
                }

                return obj;
            }

            value = pso.BaseObject;
        }

        switch (value)
        {
            case null:
                return null;
            case JsonNode node:
                return node.DeepClone();
            case JsonElement element:
                return JsonNode.Parse(element.GetRawText());
            case string s:
                return JsonValue.Create(s);
            case char c:
                return JsonValue.Create(c.ToString());
            case bool b:
                return JsonValue.Create(b);
            case Enum e:
                return JsonValue.Create(e.ToString());
            case DateTime dt:
                return JsonValue.Create(dt.ToString("O", CultureInfo.InvariantCulture));
            case DateTimeOffset dto:
                return JsonValue.Create(dto.ToString("O", CultureInfo.InvariantCulture));
            case ScriptBlock sb:
                return JsonValue.Create(sb.ToString());
            case byte or sbyte or short or ushort or int or uint or long:
                return JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case ulong or float or double or decimal:
                return JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            case IDictionary dictionary:
                var map = new JsonObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    map[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty] = ToNode(entry.Value, depth + 1);
                }

                return map;
            case IEnumerable enumerable:
                var array = new JsonArray();
                foreach (var item in enumerable)
                {
                    array.Add(ToNode(item, depth + 1));
                }

                return array;
        }

        try
        {
            return JsonSerializer.SerializeToNode(value, value.GetType(), PickleJson.Options);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            return JsonValue.Create(value.ToString());
        }
    }

    /// <summary>A PowerShell-friendly value: primitives, object[] for arrays, PSCustomObject for objects.</summary>
    public static object? ToPowerShell(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var pso = new PSObject();
                foreach (var (key, value) in obj)
                {
                    pso.Properties.Add(new PSNoteProperty(key, ToPowerShell(value)));
                }

                return pso;
            case JsonArray array:
                return array.Select(ToPowerShell).ToArray();
        }

        var element = JsonSerializer.SerializeToElement(node);
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt32(out var i) ? i : element.TryGetInt64(out var l) ? l : element.GetDouble(),
            _ => null,
        };
    }

    public static object? ToPowerShell(JsonElement element) => ToPowerShell(JsonNode.Parse(element.GetRawText()));

    /// <summary>Accepts a hashtable, PSCustomObject, JSON string or typed object and deserializes it with <see cref="PickleJson.Options"/>.</summary>
    public static T ToModel<T>(object? value)
    {
        var raw = value is PSObject pso ? pso.BaseObject : value;
        var node = raw is string text ? JsonNode.Parse(text) : ToNode(value);
        return node.Deserialize<T>(PickleJson.Options) ?? throw new ArgumentException($"Could not read a {typeof(T).Name} from the value.");
    }

    private static object? SafeValue(PSPropertyInfo property)
    {
        try
        {
            return property.Value;
        }
        catch (Exception ex) when (ex is GetValueException or RuntimeException)
        {
            return null;
        }
    }
}
