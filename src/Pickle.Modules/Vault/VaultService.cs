using System.Text.Json;
using Pickle.Abstractions;

namespace Pickle.Modules.Vault;

/// <summary>Stored under <c>extensions.vault</c>: <c>pk config set extensions.vault.backend file</c>.</summary>
public sealed class VaultSettings
{
    /// <summary><c>auto</c> (the OS store when it works, else the encrypted file), <c>credential-manager</c>, <c>libsecret</c>, <c>keychain</c> or <c>file</c>.</summary>
    public string Backend { get; set; } = "auto";

    /// <summary>How long the encrypted file stays unlocked after its password was typed; 0 asks every time.</summary>
    public int UnlockMinutes { get; set; } = 15;
}

/// <summary>The names kept in the secret stores that cannot list their own items (libsecret, Keychain). Names only, never values.</summary>
public sealed class SecretIndex(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();

    public IReadOnlyList<SecretInfo> Names(string storeId)
    {
        lock (_gate)
        {
            return Load().TryGetValue(storeId, out var names)
                ? [.. names.Select(n => new SecretInfo(n.Key, n.Value)).OrderBy(n => n.Name, StringComparer.Ordinal)]
                : [];
        }
    }

    public void Touch(string storeId, string name, DateTimeOffset when)
    {
        lock (_gate)
        {
            var all = Load();
            if (!all.TryGetValue(storeId, out var names))
            {
                all[storeId] = names = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            }

            names[name] = when;
            Save(all);
        }
    }

    public void Forget(string storeId, string name)
    {
        lock (_gate)
        {
            var all = Load();
            if (all.TryGetValue(storeId, out var names) && names.Remove(name))
            {
                Save(all);
            }
        }
    }

    private Dictionary<string, Dictionary<string, DateTimeOffset>> Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, DateTimeOffset>>>(File.ReadAllText(path), Json) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    private void Save(Dictionary<string, Dictionary<string, DateTimeOffset>> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(all, Json));
    }
}

/// <summary>Picks the secret store (the OS one, or the encrypted file) and keeps the name index in step.</summary>
public sealed class VaultService(Func<VaultSettings> settings, IReadOnlyList<ISecretStore> stores, SecretIndex index, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>In order of preference: the OS store first, the encrypted file last.</summary>
    public IReadOnlyList<ISecretStore> Stores => stores;

    public async Task<ISecretStore> ActiveAsync(CancellationToken cancellationToken)
    {
        var wanted = settings().Backend.Trim().ToLowerInvariant();
        if (wanted is "" or "auto")
        {
            foreach (var store in stores)
            {
                if (await store.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
                {
                    return store;
                }
            }

            throw new SecretStoreException("No secret store is usable on this machine.");
        }

        var chosen = stores.FirstOrDefault(s => s.Id == wanted)
            ?? throw new SecretStoreException($"'{wanted}' is not a vault backend on this system. Choose one of: auto, {string.Join(", ", stores.Select(s => s.Id))} (pk config set extensions.vault.backend <name>).");
        return await chosen.IsAvailableAsync(cancellationToken).ConfigureAwait(false)
            ? chosen
            : throw new SecretStoreException($"The '{wanted}' backend ({chosen.DisplayName}) is not usable here. Use 'auto' or 'file': pk config set extensions.vault.backend file");
    }

    public async Task SetAsync(string name, string value, CancellationToken cancellationToken)
    {
        var store = await ActiveAsync(cancellationToken).ConfigureAwait(false);
        await store.SetAsync(name, value, cancellationToken).ConfigureAwait(false);
        index.Touch(store.Id, name, _clock.GetUtcNow());
    }

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken) =>
        await (await ActiveAsync(cancellationToken).ConfigureAwait(false)).GetAsync(name, cancellationToken).ConfigureAwait(false);

    public async Task<bool> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        var store = await ActiveAsync(cancellationToken).ConfigureAwait(false);
        var removed = await store.RemoveAsync(name, cancellationToken).ConfigureAwait(false);
        index.Forget(store.Id, name);
        return removed;
    }

    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken) =>
        (await ListAsync(cancellationToken).ConfigureAwait(false)).Any(s => s.Name == name);

    public async Task<IReadOnlyList<SecretInfo>> ListAsync(CancellationToken cancellationToken)
    {
        var store = await ActiveAsync(cancellationToken).ConfigureAwait(false);
        return await store.ListAsync(cancellationToken).ConfigureAwait(false) ?? index.Names(store.Id);
    }

    public void Lock()
    {
        foreach (var store in stores)
        {
            store.Lock();
        }
    }
}
