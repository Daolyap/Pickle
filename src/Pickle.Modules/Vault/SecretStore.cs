using System.Text.RegularExpressions;

namespace Pickle.Modules.Vault;

public sealed record SecretInfo(string Name, DateTimeOffset? Updated);

/// <summary>A backend failed in a way the user can act on (wrong password, no keyring, value too large).</summary>
public sealed class SecretStoreException : InvalidOperationException
{
    public SecretStoreException(string message)
        : base(message)
    {
    }

    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Where secrets live: the OS credential store, or Pickle's encrypted file.</summary>
public interface ISecretStore
{
    /// <summary>The value of the <c>backend</c> setting that selects it.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Whether it can be used on this machine right now (the tool is installed, the keyring answers).</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);

    Task<string?> GetAsync(string name, CancellationToken cancellationToken);

    Task SetAsync(string name, string value, CancellationToken cancellationToken);

    /// <summary>True when the secret existed.</summary>
    Task<bool> RemoveAsync(string name, CancellationToken cancellationToken);

    /// <summary>The names it holds, or null when it cannot list them (the vault then uses its own name index).</summary>
    Task<IReadOnlyList<SecretInfo>?> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SecretInfo>?>(null);

    /// <summary>Forgets anything unlocked in memory.</summary>
    void Lock()
    {
    }
}

public static partial class SecretName
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex Valid();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}\z")]
    private static partial Regex ValidEnvironmentVariable();

    private static readonly HashSet<string> ProtectedVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "PSModulePath", "COMSPEC", "LD_PRELOAD", "LD_LIBRARY_PATH", "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH", "HOME", "USERPROFILE",
    };

    public static bool IsValid(string? name) => name is not null && Valid().IsMatch(name);

    /// <summary>Names are case-insensitive and kept lower-case, so every backend agrees on them.</summary>
    public static string Normalize(string? name)
    {
        if (!IsValid(name))
        {
            throw new ArgumentException("A secret name is 1-64 letters, digits, '.', '_' or '-' and starts with a letter or digit.");
        }

        return name!.ToLowerInvariant();
    }

    /// <summary><c>github-token</c> → <c>GITHUB_TOKEN</c>.</summary>
    public static string DefaultVariable(string name)
    {
        var text = new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_')]);
        return char.IsAsciiDigit(text[0]) ? "SECRET_" + text : text;
    }

    public static string RequireVariable(string variable)
    {
        if (!ValidEnvironmentVariable().IsMatch(variable))
        {
            throw new ArgumentException($"'{variable}' is not a usable environment variable name.");
        }

        if (ProtectedVariables.Contains(variable))
        {
            throw new ArgumentException($"{variable} controls how programs start, so a secret cannot be put in it.");
        }

        return variable;
    }
}
