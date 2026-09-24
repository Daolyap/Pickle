using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pickle.Abstractions;
using Pickle.Core.Commands;

namespace Pickle.Core.Config;

public enum ConfigProblemSeverity
{
    Warning,
    Error,
}

/// <summary>A problem found in config.json / config.local.json. Invalid values are ignored (defaults apply).</summary>
public sealed record ConfigProblem(ConfigProblemSeverity Severity, string Source, string Path, string Message)
{
    public override string ToString() => $"{Source}: {Message}";
}

/// <summary>A user-facing config error (unknown setting, wrong type, value out of range).</summary>
public sealed class ConfigValidationException(string message) : Exception(message);

public enum ConfigNodeKind
{
    /// <summary>A settings class such as <see cref="EditorSettings"/>.</summary>
    Section,

    /// <summary>A dictionary such as keyBindings.</summary>
    Map,

    /// <summary>A scalar or list value.</summary>
    Leaf,

    /// <summary>Anything below a JsonElement-typed value (plugin extension settings).</summary>
    FreeForm,
}

/// <summary>A resolved dotted config path, e.g. <c>editor.bellStyle</c>.</summary>
public sealed record ConfigPathInfo(IReadOnlyList<string> Segments, ConfigNodeKind Kind, Type ClrType, bool Nullable, JsonObject? Schema)
{
    public string Path => string.Join('.', Segments);
}

/// <summary>
/// The shape of <see cref="PickleConfig"/>: types come from the CLR model (so new properties are understood
/// automatically), descriptions/defaults/enums/ranges from the embedded Config/Schemas/config.schema.json.
/// </summary>
public static class ConfigSchema
{
    private static readonly Lazy<string> SchemaTextLazy = new(() =>
    {
        using var stream = typeof(ConfigSchema).Assembly.GetManifestResourceStream("Pickle.Schemas.config.schema.json")
            ?? throw new InvalidOperationException("Embedded config schema is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static readonly Lazy<JsonObject> SchemaLazy = new(() => JsonNode.Parse(SchemaTextLazy.Value)!.AsObject());

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<PropertyInfo, bool> NullableCache = new();

    public static string SchemaText => SchemaTextLazy.Value;

    public static JsonObject Schema => SchemaLazy.Value;

    // ───────────── Paths ─────────────

    public static string[] Split(string path) =>
        path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Resolve a dotted path (case-insensitive) or throw a friendly <see cref="ConfigValidationException"/>.</summary>
    public static ConfigPathInfo Resolve(string path)
    {
        var segments = Split(path);
        if (segments.Length == 0)
        {
            throw new ConfigValidationException("Config path is empty. Example: editor.bellStyle");
        }

        var canonical = new List<string>(segments.Length);
        var type = typeof(PickleConfig);
        var kind = ConfigNodeKind.Section;
        var nullable = false;
        JsonObject? schema = Schema;
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            switch (kind)
            {
                case ConfigNodeKind.Section:
                    var property = FindProperty(type, segment);
                    if (property is null)
                    {
                        throw Unknown(string.Join('.', canonical.Append(segment)));
                    }

                    canonical.Add(JsonName(property));
                    type = property.PropertyType;
                    nullable = IsNullable(property);
                    kind = KindOf(type);
                    schema = SchemaProperty(schema, canonical[^1]);
                    break;
                case ConfigNodeKind.Map:
                    canonical.Add(segment);
                    type = type.GetGenericArguments()[1];
                    nullable = false;
                    kind = type == typeof(JsonElement) ? ConfigNodeKind.FreeForm : KindOf(type);
                    schema = schema?["additionalProperties"] as JsonObject;
                    break;
                case ConfigNodeKind.FreeForm:
                    canonical.Add(segment);
                    schema = null;
                    break;
                default:
                    throw new ConfigValidationException($"'{string.Join('.', canonical)}' is a single value and has no setting named '{segment}'.");
            }
        }

        return new ConfigPathInfo(canonical, kind, type, nullable, schema);
    }

    /// <summary>Every settable leaf path (dotted camelCase), for listing and "did you mean".</summary>
    public static IReadOnlyList<string> AllPaths()
    {
        var result = new List<string>();
        Walk(typeof(PickleConfig), string.Empty, result);
        return result;

        static void Walk(Type type, string prefix, List<string> result)
        {
            foreach (var property in SettableProperties(type))
            {
                var path = prefix + JsonName(property);
                result.Add(path);
                if (KindOf(property.PropertyType) == ConfigNodeKind.Section)
                {
                    Walk(property.PropertyType, path + ".", result);
                }
            }
        }
    }

    public static string? Description(string path) => TryResolve(path)?.Schema?["description"]?.GetValue<string>();

    public static ConfigPathInfo? TryResolve(string path)
    {
        try
        {
            return Resolve(path);
        }
        catch (ConfigValidationException)
        {
            return null;
        }
    }

    // ───────────── Conversion (pk config set) ─────────────

    /// <summary>Convert command-line text to the JSON value for <paramref name="info"/>, validating it.</summary>
    public static JsonNode? Convert(ConfigPathInfo info, string raw)
    {
        var text = raw.Trim();
        JsonNode? node;
        switch (info.Kind)
        {
            case ConfigNodeKind.Section:
            case ConfigNodeKind.Map:
                node = ParseJson(text) as JsonObject
                    ?? throw new ConfigValidationException($"'{info.Path}' is a group of settings; pass a JSON object like {{\"key\": value}} or set a single value such as '{info.Path}.<name>'.");
                break;
            case ConfigNodeKind.FreeForm:
                node = ParseJson(text) ?? (text == "null" ? null : JsonValue.Create(raw));
                break;
            default:
                node = ConvertLeaf(info, text);
                break;
        }

        var problems = new List<ConfigProblem>();
        ValidateValue(node, info.ClrType, info.Nullable, info.Kind, info.Schema, info.Path, "value", problems, removeInvalid: false);
        if (problems.FirstOrDefault(p => p.Severity == ConfigProblemSeverity.Error) is { } error)
        {
            throw new ConfigValidationException(error.Message);
        }

        return node;
    }

    private static JsonNode? ConvertLeaf(ConfigPathInfo info, string text)
    {
        var type = Underlying(info.ClrType);
        if (info.Nullable && text is "null" or "$null")
        {
            return null;
        }

        if (type == typeof(bool))
        {
            return text.ToLowerInvariant() switch
            {
                "true" or "$true" or "yes" or "on" or "1" => JsonValue.Create(true),
                "false" or "$false" or "no" or "off" or "0" => JsonValue.Create(false),
                _ => throw new ConfigValidationException($"Invalid value '{text}' for {info.Path}: expected true or false."),
            };
        }

        if (type == typeof(int) || type == typeof(long))
        {
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? JsonValue.Create(n)
                : throw new ConfigValidationException($"Invalid value '{text}' for {info.Path}: expected a whole number.");
        }

        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? JsonValue.Create(d)
                : throw new ConfigValidationException($"Invalid value '{text}' for {info.Path}: expected a number.");
        }

        if (type == typeof(string))
        {
            var value = Unquote(text);
            if (EnumValues(info.Schema) is { } allowed)
            {
                value = allowed.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase)) ?? value;
            }

            return JsonValue.Create(value);
        }

        if (IsStringList(type))
        {
            if (text.StartsWith('['))
            {
                return ParseJson(text) ?? throw new ConfigValidationException($"Invalid value for {info.Path}: '{text}' is not a valid JSON array.");
            }

            var items = text.Length == 0 ? [] : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new JsonArray([.. items.Select(i => (JsonNode?)JsonValue.Create(Unquote(i)))]);
        }

        return ParseJson(text) ?? JsonValue.Create(text);
    }

    private static string Unquote(string text)
    {
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(text) ?? text;
            }
            catch (JsonException)
            {
            }
        }

        return text;
    }

    private static JsonNode? ParseJson(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ───────────── Validation ─────────────

    /// <summary>
    /// Check a config layer. With <paramref name="removeInvalid"/>, invalid values are removed so the rest of the
    /// file still applies (the defaults take their place).
    /// </summary>
    public static IReadOnlyList<ConfigProblem> Validate(JsonObject root, string source, bool removeInvalid)
    {
        var problems = new List<ConfigProblem>();
        ValidateSection(root, typeof(PickleConfig), Schema, string.Empty, source, problems, removeInvalid);
        return problems;
    }

    private static void ValidateSection(JsonObject obj, Type type, JsonObject? schema, string prefix, string source, List<ConfigProblem> problems, bool removeInvalid)
    {
        foreach (var (key, value) in obj.ToList())
        {
            if (prefix.Length == 0 && key == "$schema")
            {
                continue;
            }

            var path = prefix + key;
            var property = FindProperty(type, key);
            if (property is null)
            {
                var hint = Fuzzy.Suggest(path, AllPaths(), 1) is [var best] ? $" Did you mean '{best}'?" : string.Empty;
                problems.Add(new ConfigProblem(ConfigProblemSeverity.Warning, source, path, $"Unknown setting '{path}' is ignored.{hint}"));
                continue;
            }

            var propertySchema = SchemaProperty(schema, JsonName(property));
            var kind = KindOf(property.PropertyType);
            if (!ValidateValue(value, property.PropertyType, IsNullable(property), kind, propertySchema, path, source, problems, removeInvalid) && removeInvalid)
            {
                obj.Remove(key);
            }
        }
    }

    /// <returns>False when the value itself is invalid (and should be dropped).</returns>
    private static bool ValidateValue(JsonNode? value, Type type, bool nullable, ConfigNodeKind kind, JsonObject? schema, string path, string source, List<ConfigProblem> problems, bool removeInvalid)
    {
        bool Fail(string message)
        {
            problems.Add(new ConfigProblem(ConfigProblemSeverity.Error, source, path, message));
            return false;
        }

        if (value is null)
        {
            return nullable || kind == ConfigNodeKind.FreeForm || Fail($"Invalid value for {path}: null is not allowed.");
        }

        switch (kind)
        {
            case ConfigNodeKind.Section:
                if (value is not JsonObject section)
                {
                    return Fail($"Invalid value for {path}: expected an object with settings.");
                }

                ValidateSection(section, type, schema, path + ".", source, problems, removeInvalid);
                return true;
            case ConfigNodeKind.Map:
                if (value is not JsonObject map)
                {
                    return Fail($"Invalid value for {path}: expected an object.");
                }

                var valueType = type.GetGenericArguments()[1];
                if (valueType != typeof(JsonElement))
                {
                    foreach (var (key, entry) in map.ToList())
                    {
                        if (!ValidateValue(entry, valueType, false, KindOf(valueType), schema?["additionalProperties"] as JsonObject, $"{path}.{key}", source, problems, removeInvalid) && removeInvalid)
                        {
                            map.Remove(key);
                        }
                    }
                }

                return true;
            case ConfigNodeKind.FreeForm:
                return true;
        }

        var error = CheckLeaf(value, Underlying(type), schema);
        return error is null || Fail($"Invalid value {Show(value)} for {path}: {error}.");
    }

    private static string? CheckLeaf(JsonNode value, Type type, JsonObject? schema)
    {
        if (type == typeof(bool))
        {
            return value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? null : "expected true or false";
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            if (value.GetValueKind() != JsonValueKind.Number)
            {
                return type == typeof(int) || type == typeof(long) ? "expected a whole number" : "expected a number";
            }

            var number = ToDouble(value);
            if ((type == typeof(int) && (number != Math.Floor(number) || number < int.MinValue || number > int.MaxValue))
                || (type == typeof(long) && number != Math.Floor(number)))
            {
                return "expected a whole number";
            }

            if (schema?["minimum"] is JsonValue min && number < ToDouble(min))
            {
                return $"must be at least {min.ToJsonString()}";
            }

            if (schema?["maximum"] is JsonValue max && number > ToDouble(max))
            {
                return $"must be at most {max.ToJsonString()}";
            }

            return null;
        }

        if (type == typeof(string))
        {
            if (value.GetValueKind() != JsonValueKind.String)
            {
                return "expected text";
            }

            var text = value.GetValue<string>();
            if (EnumValues(schema) is { } allowed && !allowed.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                return $"expected one of: {string.Join(", ", allowed)}";
            }

            return CheckPattern(text, schema);
        }

        if (IsStringList(type))
        {
            if (value is not JsonArray array)
            {
                return "expected a list, e.g. [\"a\", \"b\"]";
            }

            foreach (var item in array)
            {
                if (item?.GetValueKind() != JsonValueKind.String)
                {
                    return "expected a list of text values";
                }

                if (CheckPattern(item.GetValue<string>(), schema?["items"] as JsonObject) is { } itemError)
                {
                    return $"'{item.GetValue<string>()}' {itemError}";
                }
            }

            return null;
        }

        return null;
    }

    private static string? CheckPattern(string text, JsonObject? schema)
    {
        if (schema?["pattern"] is JsonValue pattern && !Regex.IsMatch(text, pattern.GetValue<string>(), RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            return $"does not match the expected format ({pattern})";
        }

        return null;
    }

    private static string Show(JsonNode value) => value.ToJsonString();

    private static double ToDouble(JsonNode value) => double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    public static IReadOnlyList<string>? EnumValues(JsonObject? schema) =>
        schema?["enum"] is JsonArray values ? [.. values.Select(v => v!.GetValue<string>())] : null;

    // ───────────── CLR model ─────────────

    public static string JsonName(PropertyInfo property) => JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    public static IEnumerable<PropertyInfo> SettableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0);

    private static PropertyInfo? FindProperty(Type type, string jsonName) =>
        SettableProperties(type).FirstOrDefault(p => string.Equals(JsonName(p), jsonName, StringComparison.OrdinalIgnoreCase));

    // NullabilityInfoContext is not thread-safe, so each lookup gets its own and results are cached.
    private static bool IsNullable(PropertyInfo property) =>
        NullableCache.GetOrAdd(property, p =>
            Nullable.GetUnderlyingType(p.PropertyType) is not null
            || (!p.PropertyType.IsValueType && new NullabilityInfoContext().Create(p).WriteState == NullabilityState.Nullable));

    public static ConfigNodeKind KindOf(Type type)
    {
        if (type == typeof(JsonElement))
        {
            return ConfigNodeKind.FreeForm;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            return ConfigNodeKind.Map;
        }

        var underlying = Underlying(type);
        return underlying.IsPrimitive || underlying == typeof(string) || underlying == typeof(decimal) || IsStringList(underlying) || underlying.IsEnum
            ? ConfigNodeKind.Leaf
            : ConfigNodeKind.Section;
    }

    private static Type Underlying(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsStringList(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) && type.GetGenericArguments()[0] == typeof(string);

    private static JsonObject? SchemaProperty(JsonObject? schema, string name) =>
        schema?["properties"] is JsonObject properties
            ? properties.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value as JsonObject
            : null;

    private static ConfigValidationException Unknown(string path)
    {
        var suggestions = Fuzzy.Suggest(path, AllPaths());
        var hint = suggestions.Count > 0 ? $" Did you mean {string.Join(" or ", suggestions.Select(s => $"'{s}'"))}?" : " Run 'pk config' to see all settings.";
        return new ConfigValidationException($"Unknown setting '{path}'.{hint}");
    }
}
