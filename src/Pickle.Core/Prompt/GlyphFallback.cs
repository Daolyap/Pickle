using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Prompt;

/// <summary>
/// A copy of a theme that draws without Nerd Font glyphs, for terminals whose font lacks them (Windows Terminal's
/// default Cascadia Mono has none, so the default theme's branch icon showed as "�"). Private-use glyphs become a
/// Unicode look-alike or disappear with their padding space, and Powerline, round and slant separators become plain.
/// </summary>
public static class GlyphFallback
{
    private static readonly Dictionary<int, string> LookAlikes = new()
    {
        [0xF0E7] = "⚡", // nf-fa-bolt (elevated)
        [0xF00D] = "✘", // nf-fa-times (failed command)
        [0xF00C] = "✔", // nf-fa-check
        [0xF013] = "⚙", // nf-fa-cog (jobs)
        [0xF071] = "⚠", // nf-fa-warning
    };

    private static readonly ConditionalWeakTable<Theme, Theme> Cache = [];

    /// <summary>The theme itself when it has nothing to replace.</summary>
    public static Theme ForUnicode(Theme theme) => Cache.GetValue(theme, Convert);

    /// <summary>Replaces private-use glyphs; a removed glyph also takes one neighboring space with it.</summary>
    public static string Replace(string text)
    {
        if (!HasPrivateUse(text))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var dropSpace = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (NerdFonts.IsPrivateUse(rune.Value))
            {
                var replacement = LookAlikes.GetValueOrDefault(rune.Value, string.Empty);
                sb.Append(replacement);
                dropSpace = replacement.Length == 0 && (sb.Length == 0 || sb[^1] == ' ');
                continue;
            }

            if (dropSpace && rune.Value == ' ')
            {
                dropSpace = false;
                continue;
            }

            dropSpace = false;
            sb.Append(rune.ToString());
        }

        return sb.ToString();
    }

    public static bool HasPrivateUse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var rune in text.EnumerateRunes())
        {
            if (NerdFonts.IsPrivateUse(rune.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static Theme Convert(Theme theme)
    {
        var prompt = theme.Prompt;
        var glyphSeparator = prompt.Separator is SeparatorStyle.Powerline or SeparatorStyle.Round or SeparatorStyle.Slant;
        var styles = prompt.Left.Concat(prompt.Right).ToList();
        if (!glyphSeparator
            && !styles.Any(s => HasPrivateUse(s.Icon) || HasPrivateUse(s.Template) || s.Options.Values.Any(HasPrivateUse))
            && !HasPrivateUse(prompt.PromptChar) && !HasPrivateUse(prompt.ContinuationPrompt) && !HasPrivateUse(prompt.TransientTemplate))
        {
            return theme;
        }

        var copy = JsonSerializer.Deserialize<Theme>(JsonSerializer.Serialize(theme, PickleJson.Compact), PickleJson.Compact) ?? theme;
        var p = copy.Prompt;
        if (glyphSeparator)
        {
            p.Separator = SeparatorStyle.Plain;
        }

        foreach (var style in p.Left.Concat(p.Right))
        {
            if (style.Icon is { } icon)
            {
                style.Icon = Replace(icon).Trim() is { Length: > 0 } kept ? kept : null;
            }

            if (style.Template is { } template)
            {
                style.Template = Replace(template);
            }

            foreach (var (key, value) in style.Options.Where(o => HasPrivateUse(o.Value)).ToList())
            {
                var replaced = Replace(value).Trim();
                if (replaced.Length == 0)
                {
                    style.Options.Remove(key); // the segment's own default symbol takes over
                }
                else
                {
                    style.Options[key] = replaced;
                }
            }
        }

        p.PromptChar = Replace(p.PromptChar) is { Length: > 0 } promptChar ? promptChar : "❯";
        p.ContinuationPrompt = Replace(p.ContinuationPrompt);
        p.TransientTemplate = Replace(p.TransientTemplate);
        return copy;
    }
}
