using System.Text.Json;

namespace Pickle.Abstractions;

/// <summary>
/// Typed settings for a plugin or module, stored under <c>extensions.&lt;id&gt;</c> in config.json (so
/// <c>pk config set extensions.docker.showStopped true</c> works). Missing or invalid values give the defaults.
/// </summary>
public static class ModuleConfig
{
    public static T Get<T>(this IConfigStore config, string id)
        where T : class, new()
    {
        if (!config.Current.Extensions.TryGetValue(id, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return new T();
        }

        try
        {
            return element.Deserialize<T>(PickleJson.Options) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }

    public static void Set<T>(this IConfigStore config, string id, T settings)
        where T : class =>
        config.Update(c => c.Extensions[id] = JsonSerializer.SerializeToElement(settings, PickleJson.Options));
}
