using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Pickle.Windows;

/// <summary>Strict validation for every identifier that reaches winget, WUA, Task Scheduler or the elevated helper.</summary>
public static partial class WindowsIds
{
    public const int MaxSearchQueryLength = 256;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_+]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex WingetIdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_+]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex WingetVersionRegex();

    [GeneratedRegex(@"^(?:KB)?(\d{4,8})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KbRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex TaskNameRegex();

    [GeneratedRegex(@"^\\(?:[A-Za-z0-9][A-Za-z0-9 ._\-]{0,63}(?:\\[A-Za-z0-9][A-Za-z0-9 ._\-]{0,63})*\\?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TaskFolderRegex();

    public static bool IsValidWingetId([NotNullWhen(true)] string? id) => id is not null && WingetIdRegex().IsMatch(id);

    public static bool IsValidWingetVersion([NotNullWhen(true)] string? version) => version is not null && WingetVersionRegex().IsMatch(version);

    /// <summary>Throws <see cref="ArgumentException"/> with a user-facing message when <paramref name="id"/> is not a plain winget package id.</summary>
    public static string RequireWingetId(string? id)
    {
        if (IsValidWingetId(id))
        {
            return id;
        }

        if (id is not null && id.Contains('…', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The package id '{id}' was truncated by winget's text output. Install Microsoft.WinGet.Client (pk winget install-module) or use the full id.");
        }

        throw new ArgumentException($"'{id}' is not a valid winget package id (letters, digits and . - _ + only).");
    }

    public static string RequireWingetVersion(string? version) =>
        IsValidWingetVersion(version) ? version : throw new ArgumentException($"'{version}' is not a valid package version.");

    public static string RequireSearchQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Search query is empty.");
        }

        if (query.Length > MaxSearchQueryLength || query.Any(char.IsControl))
        {
            throw new ArgumentException("Search query is too long or contains control characters.");
        }

        return query.Trim();
    }

    /// <summary>WUA update ids are GUIDs; returns the canonical lowercase "D" form.</summary>
    public static bool TryNormalizeUpdateId(string? value, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (value is null || value.Length > 38)
        {
            return false;
        }

        var text = value.Trim();
        if (!Guid.TryParseExact(text, "D", out var guid) && !Guid.TryParseExact(text, "B", out guid))
        {
            return false;
        }

        normalized = guid.ToString("D", CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Accepts "KB5031455" or "5031455"; returns "KB5031455".</summary>
    public static bool TryNormalizeKb(string? value, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (value is null)
        {
            return false;
        }

        var match = KbRegex().Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        normalized = "KB" + match.Groups[1].Value;
        return true;
    }

    public static bool IsValidTaskName([NotNullWhen(true)] string? name) => name is not null && TaskNameRegex().IsMatch(name);

    /// <summary>Folders like <c>\</c>, <c>\Pickle</c>, <c>\Pickle\Test</c>.</summary>
    public static bool IsValidTaskFolder([NotNullWhen(true)] string? folder) =>
        folder is not null && folder.Length <= 260 && TaskFolderRegex().IsMatch(folder);

    /// <summary>True for <c>\Pickle</c> and anything below it (the only place the elevated helper may register tasks).</summary>
    public static bool IsPickleTaskFolder(string folder)
    {
        var trimmed = folder.TrimEnd('\\');
        return string.Equals(trimmed, @"\Pickle", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(@"\Pickle\", StringComparison.OrdinalIgnoreCase);
    }
}
