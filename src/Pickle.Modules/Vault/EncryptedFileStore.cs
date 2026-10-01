using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pickle.Modules.Vault;

/// <summary>
/// Pickle's own vault for machines without a usable OS store (a headless server, a container): one JSON file of AES-256-GCM
/// entries under a key derived from a master password with PBKDF2-SHA256. The key is cached in memory for a while after the
/// password was typed; names are stored in clear (they are not secret), values never are.
/// </summary>
public sealed class EncryptedFileStore(
    string path,
    Func<string, CancellationToken, Task<string?>> askPassword,
    Func<TimeSpan> unlockFor,
    TimeProvider? clock = null,
    int iterations = EncryptedFileStore.DefaultIterations) : ISecretStore
{
    public const int DefaultIterations = 600_000;
    public const int MinimumPasswordLength = 8;
    private const int MaxIterations = 10_000_000;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const string CheckText = "pickle vault";
    private const string CheckName = "\0check";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private byte[]? _key;
    private string? _keySalt;
    private DateTimeOffset _keyExpires;

    public string Id => "file";

    public string DisplayName => "Encrypted file";

    public string FilePath => path;

    public bool IsUnlocked
    {
        get
        {
            lock (_gate)
            {
                return _key is not null && _clock.GetUtcNow() < _keyExpires;
            }
        }
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(AesGcm.IsSupported);

    public void Lock()
    {
        lock (_gate)
        {
            if (_key is not null)
            {
                CryptographicOperations.ZeroMemory(_key);
            }

            _key = null;
            _keySalt = null;
        }
    }

    public Task<IReadOnlyList<SecretInfo>?> ListAsync(CancellationToken cancellationToken)
    {
        var file = Load();
        IReadOnlyList<SecretInfo> list = file is null ? [] : [.. file.Secrets.Select(s => new SecretInfo(s.Key, s.Value.Updated)).OrderBy(s => s.Name, StringComparer.Ordinal)];
        return Task.FromResult<IReadOnlyList<SecretInfo>?>(list);
    }

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        var file = Load();
        if (file is null || !file.Secrets.TryGetValue(name, out var entry))
        {
            return null;
        }

        var key = await UnlockAsync(file, cancellationToken).ConfigureAwait(false);
        try
        {
            return Encoding.UTF8.GetString(Open(key, entry, name));
        }
        catch (CryptographicException ex)
        {
            throw new SecretStoreException($"'{name}' could not be decrypted; the vault file was changed outside Pickle.", ex);
        }
    }

    public async Task SetAsync(string name, string value, CancellationToken cancellationToken)
    {
        var file = Load();
        byte[] key;
        if (file is null)
        {
            (file, key) = await CreateAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            key = await UnlockAsync(file, cancellationToken).ConfigureAwait(false);
        }

        var entry = Seal(key, Encoding.UTF8.GetBytes(value), name);
        entry.Updated = _clock.GetUtcNow();
        file.Secrets[name] = entry;
        Save(file);
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        var file = Load();
        if (file is null || !file.Secrets.ContainsKey(name))
        {
            return false;
        }

        await UnlockAsync(file, cancellationToken).ConfigureAwait(false);
        file.Secrets.Remove(name);
        Save(file);
        return true;
    }

    private async Task<(VaultFile File, byte[] Key)> CreateAsync(CancellationToken cancellationToken)
    {
        var first = await askPassword("New vault password: ", cancellationToken).ConfigureAwait(false)
            ?? throw new SecretStoreException("There is no vault yet, and a new one needs a master password typed at the terminal.");
        if (first.Length < MinimumPasswordLength)
        {
            throw new SecretStoreException($"Use a master password of at least {MinimumPasswordLength} characters.");
        }

        if (await askPassword("Repeat it: ", cancellationToken).ConfigureAwait(false) != first)
        {
            throw new SecretStoreException("The two passwords differ; no vault was created.");
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Derive(first, salt, iterations);
        var file = new VaultFile { Iterations = iterations, Salt = Convert.ToBase64String(salt) };
        file.Check = Seal(key, Encoding.UTF8.GetBytes(CheckText), CheckName);
        Remember(key, file.Salt);
        return (file, key);
    }

    private async Task<byte[]> UnlockAsync(VaultFile file, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_key is { } cached && _keySalt == file.Salt && _clock.GetUtcNow() < _keyExpires)
            {
                return cached;
            }
        }

        if (file.Iterations is < 1 or > MaxIterations)
        {
            throw new SecretStoreException("The vault file has an unsupported key setting; it may be damaged.");
        }

        var salt = DecodeBase64(file.Salt);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var password = await askPassword("Vault password: ", cancellationToken).ConfigureAwait(false)
                ?? throw new SecretStoreException("The vault is locked and Pickle cannot ask for its password here (run this at an interactive prompt).");
            var key = Derive(password, salt, file.Iterations);
            try
            {
                if (Encoding.UTF8.GetString(Open(key, file.Check, CheckName)) == CheckText)
                {
                    Remember(key, file.Salt);
                    return key;
                }
            }
            catch (CryptographicException)
            {
            }

            CryptographicOperations.ZeroMemory(key);
        }

        throw new SecretStoreException("Wrong vault password.");
    }

    private void Remember(byte[] key, string salt)
    {
        lock (_gate)
        {
            if (_key is not null && !ReferenceEquals(_key, key))
            {
                CryptographicOperations.ZeroMemory(_key);
            }

            _key = key;
            _keySalt = salt;
            _keyExpires = _clock.GetUtcNow() + unlockFor();
        }
    }

    private static byte[] Derive(string password, byte[] salt, int iterationCount) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterationCount, HashAlgorithmName.SHA256, KeyBytes);

    private static Entry Seal(byte[] key, byte[] plain, string name)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using var gcm = new AesGcm(key, TagBytes);
        gcm.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(name));
        return new Entry { Nonce = Convert.ToBase64String(nonce), Data = Convert.ToBase64String(cipher), Tag = Convert.ToBase64String(tag) };
    }

    // The name is authenticated data, so an entry copied under another name does not decrypt.
    private static byte[] Open(byte[] key, Entry entry, string name)
    {
        var cipher = DecodeBase64(entry.Data);
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, TagBytes);
        gcm.Decrypt(DecodeBase64(entry.Nonce), cipher, DecodeBase64(entry.Tag), plain, Encoding.UTF8.GetBytes(name));
        return plain;
    }

    private static byte[] DecodeBase64(string text)
    {
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException ex)
        {
            throw new SecretStoreException("The vault file is damaged (bad encoding).", ex);
        }
    }

    private VaultFile? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var file = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(path), Json);
            return file is { Version: 1 } ? file : throw new SecretStoreException("The vault file is from a different version of Pickle.");
        }
        catch (JsonException ex)
        {
            throw new SecretStoreException($"The vault file {path} is damaged: {ex.Message}", ex);
        }
    }

    private void Save(VaultFile file)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temporary, options))
        {
            JsonSerializer.Serialize(stream, file, Json);
        }

        File.Move(temporary, path, overwrite: true);
    }

    internal sealed class Entry
    {
        public string Nonce { get; set; } = string.Empty;

        public string Data { get; set; } = string.Empty;

        public string Tag { get; set; } = string.Empty;

        public DateTimeOffset? Updated { get; set; }
    }

    internal sealed class VaultFile
    {
        public int Version { get; set; } = 1;

        public string Kdf { get; set; } = "pbkdf2-sha256";

        public int Iterations { get; set; }

        public string Salt { get; set; } = string.Empty;

        public Entry Check { get; set; } = new();

        public Dictionary<string, Entry> Secrets { get; set; } = new(StringComparer.Ordinal);
    }
}
