using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pickle.Windows.Terminal;

/// <summary>
/// Works out which font Windows Terminal draws the current tab with. Terminal sets <c>WT_PROFILE_ID</c>; the face comes
/// from that profile's entry in settings.json, then (for Pickle's profile) the per-user fragment, then the all-users
/// fragment, then <c>profiles.defaults</c>, then Terminal's default. That is the order Terminal layers them in: per-user
/// fragments load before, and so override, the ones under ProgramData.
/// </summary>
public static class TerminalFontProbe
{
    public const string TerminalDefaultFont = "Cascadia Mono";

    private static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The font face (possibly a comma-separated fallback list), or null when not running in Windows Terminal.</summary>
    public static string? WindowsTerminalFace(WindowsTerminalLocations locations, Func<string, string?> environment)
    {
        if (environment("WT_SESSION") is not { Length: > 0 })
        {
            return null;
        }

        var profileId = environment("WT_PROFILE_ID")?.Trim();
        var settings = locations.SettingsFiles.Select(Read).OfType<JsonObject>().ToList();
        var active = settings.FirstOrDefault(s => profileId is not null && FindProfile(s, profileId) is not null) ?? settings.FirstOrDefault();

        if (profileId is not null && active is not null && FindProfile(active, profileId) is { } entry && Face(entry) is { } own)
        {
            return own;
        }

        if (profileId is not null && profileId.Equals(WindowsTerminalFragment.ProfileGuidString, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var fragment in new[] { locations.FragmentFile, locations.MachineFragmentFile })
            {
                if (fragment is not null && Read(fragment) is JsonObject root && FindProfile(root, profileId) is { } profile && Face(profile) is { } face)
                {
                    return face;
                }
            }
        }

        if (active?["profiles"] is JsonObject profiles && profiles["defaults"] is JsonObject defaults && Face(defaults) is { } fallback)
        {
            return fallback;
        }

        return TerminalDefaultFont;
    }

    /// <summary>A profile by GUID from settings.json ("profiles": {"list": [...]} or a bare array) or a fragment.</summary>
    private static JsonObject? FindProfile(JsonObject root, string guid)
    {
        var list = root["profiles"] switch
        {
            JsonArray array => array,
            JsonObject obj => obj["list"] as JsonArray,
            _ => null,
        };

        return list?.OfType<JsonObject>().FirstOrDefault(p =>
            p["guid"] is JsonValue value && value.TryGetValue<string>(out var id) && id.Equals(guid, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Face(JsonObject profile)
    {
        var face = profile["font"] is JsonObject font ? Text(font["face"]) : null;
        return face ?? Text(profile["fontFace"]);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static JsonNode? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: Jsonc) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
