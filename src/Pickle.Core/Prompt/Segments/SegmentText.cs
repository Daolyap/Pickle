using System.Globalization;
using System.Text;
using Pickle.Abstractions;

namespace Pickle.Core.Prompt.Segments;

/// <summary>Helpers shared by segments.</summary>
public static class SegmentText
{
    /// <summary>
    /// Replaces control characters with '?'. Directory names, env vars and file contents end up in the prompt, and
    /// an embedded ESC would let them inject terminal escape sequences.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder? sb = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsControl(c))
            {
                sb ??= new StringBuilder(text, 0, i, text.Length);
                sb.Append('?');
            }
            else
            {
                sb?.Append(c);
            }
        }

        return sb?.ToString() ?? text;
    }

    public static string? Option(this SegmentStyle style, string name) =>
        style.Options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    public static int OptionInt(this SegmentStyle style, string name, int fallback) =>
        int.TryParse(style.Option(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public static bool OptionBool(this SegmentStyle style, string name, bool fallback) =>
        style.Option(name)?.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => fallback,
        };

    public static ValueTask<PromptSegmentOutput?> Show(string text) => ValueTask.FromResult<PromptSegmentOutput?>(new PromptSegmentOutput(text));

    public static ValueTask<PromptSegmentOutput?> Hide() => ValueTask.FromResult<PromptSegmentOutput?>(null);
}
