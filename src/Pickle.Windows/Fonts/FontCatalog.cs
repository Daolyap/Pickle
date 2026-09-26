using Pickle.Abstractions.Services;

namespace Pickle.Windows.Fonts;

/// <summary>
/// Answers "is this family installed" from the names Windows registers fonts under
/// (<c>HKLM|HKCU\Software\Microsoft\Windows NT\CurrentVersion\Fonts</c>: "Cascadia Code NF Regular (TrueType)",
/// "Cambria &amp; Cambria Math (TrueType)"). A family matches a name that is the family itself or the family followed
/// only by style words, so "Cascadia Code" does not match "Cascadia Code NF Regular".
/// </summary>
public sealed class FontCatalog
{
    /// <summary>Windows Terminal ships these inside its package, so they are available to it without being registered.</summary>
    public static readonly IReadOnlyList<string> TerminalBundled = ["Cascadia Mono", "Cascadia Code"];

    private static readonly HashSet<string> StyleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Regular", "Normal", "Book", "Roman", "Italic", "Oblique", "Bold", "Light", "Thin", "Medium", "Black", "Heavy",
        "ExtraLight", "UltraLight", "SemiLight", "DemiLight", "SemiBold", "DemiBold", "ExtraBold", "UltraBold",
        "Extra", "Ultra", "Semi", "Demi", "Condensed", "Narrow", "Variable",
    };

    private readonly List<string> _faces;

    public FontCatalog(IEnumerable<string> registryNames)
    {
        _faces = [.. registryNames.SelectMany(Faces).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Every face name found, without the "(TrueType)" suffix.</summary>
    public IReadOnlyList<string> Faces() => _faces;

    public bool Contains(string family)
    {
        family = family.Trim();
        return family.Length > 0 && _faces.Any(face => Matches(face, family));
    }

    /// <summary>Families (style words removed) of the installed Nerd Fonts, sorted.</summary>
    public IReadOnlyList<string> NerdFontFamilies() =>
    [
        .. _faces.Where(NerdFonts.IsNerdFontName).Select(FamilyOf).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>Splits a registry value name into face names: "A &amp; B (TrueType)" → A, B.</summary>
    public static IEnumerable<string> Faces(string registryName)
    {
        var name = registryName.Trim();
        var paren = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (paren > 0 && name.EndsWith(')'))
        {
            name = name[..paren];
        }

        return name.Split(" & ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static bool Matches(string face, string family)
    {
        if (face.Equals(family, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!face.StartsWith(family + " ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return face[(family.Length + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).All(StyleWords.Contains);
    }

    /// <summary>"Cascadia Code NF SemiBold Italic" → "Cascadia Code NF".</summary>
    public static string FamilyOf(string face)
    {
        var words = face.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && StyleWords.Contains(words[^1]))
        {
            words.RemoveAt(words.Count - 1);
        }

        return string.Join(' ', words);
    }
}
