using System.Text;
using Pickle.Abstractions.Services;

namespace Pickle.Modules.Vault;

/// <summary>
/// The GNOME Keyring / KWallet / KeePassXC secret service through <c>secret-tool</c> (libsecret). Items carry the
/// attributes <c>service=pickle account=&lt;name&gt;</c>, so any libsecret tool can read them.
/// </summary>
public sealed class LibSecretStore(Func<IProgramRunner> runnerFactory) : ISecretStore
{
    private const string Program = "secret-tool";
    private bool? _available;

    private IProgramRunner Runner => runnerFactory();

    public string Id => "libsecret";

    public string DisplayName => "Secret Service (libsecret)";

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (_available is { } known)
        {
            return known;
        }

        if (Runner.Find(Program) is null)
        {
            return false;
        }

        // A lookup of a name that cannot exist exits 1 silently when the keyring answers and complains on stderr when it does not (no D-Bus, no keyring).
        var probe = await Runner.RunAsync(Program, ["lookup", "service", "pickle", "account", ".probe"], new ProgramRunOptions { Timeout = TimeSpan.FromSeconds(10) }, cancellationToken).ConfigureAwait(false);
        _available = probe.WasFound && probe.ExitCode != ProgramResult.TimedOut && (probe.Success || string.IsNullOrWhiteSpace(probe.StdErr));
        return _available.Value;
    }

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        var result = await Runner.RunAsync(Program, ["lookup", "service", "pickle", "account", name], null, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            return result.StdOut;
        }

        return string.IsNullOrWhiteSpace(result.StdErr) ? null : throw new SecretStoreException("secret-tool: " + result.Message);
    }

    public async Task SetAsync(string name, string value, CancellationToken cancellationToken)
    {
        var result = await Runner.RunAsync(
            Program,
            ["store", "--label", "Pickle: " + name, "service", "pickle", "account", name],
            new ProgramRunOptions { StandardInput = value, Timeout = TimeSpan.FromSeconds(120) },
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new SecretStoreException("secret-tool could not store the secret: " + result.Message);
        }
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        if (await GetAsync(name, cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        var result = await Runner.RunAsync(Program, ["clear", "service", "pickle", "account", name], null, cancellationToken).ConfigureAwait(false);
        return result.Success ? true : throw new SecretStoreException("secret-tool could not remove the secret: " + result.Message);
    }
}

/// <summary>
/// The macOS login keychain through <c>security</c>. Commands go to <c>security -i</c> on standard input so the secret never
/// appears in a process list, and the value is stored as base64 text because <c>security -w</c> prints anything it considers
/// non-printable as hex.
/// </summary>
public sealed class KeychainStore(Func<IProgramRunner> runnerFactory) : ISecretStore
{
    private const string Program = "security";
    private const int ItemNotFound = 44;

    private IProgramRunner Runner => runnerFactory();

    public string Id => "keychain";

    public string DisplayName => "macOS Keychain";

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(OperatingSystem.IsMacOS() && Runner.Find(Program) is not null);

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        var result = await Runner.RunAsync(Program, ["find-generic-password", "-a", name, "-s", "pickle", "-w"], null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == ItemNotFound)
        {
            return null;
        }

        if (!result.Success)
        {
            throw new SecretStoreException("security could not read the secret: " + result.Message);
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(result.StdOut.Trim()));
        }
        catch (FormatException)
        {
            return result.StdOut.TrimEnd('\r', '\n');
        }
    }

    public async Task SetAsync(string name, string value, CancellationToken cancellationToken)
    {
        // The name is already restricted to [A-Za-z0-9._-], so nothing here needs quoting beyond the label's space.
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        var result = await Runner.RunAsync(
            Program,
            ["-i"],
            new ProgramRunOptions { StandardInput = $"add-generic-password -a {name} -s pickle -l \"Pickle: {name}\" -U -w {encoded}\n" },
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new SecretStoreException("security could not store the secret: " + result.Message);
        }
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        var result = await Runner.RunAsync(Program, ["delete-generic-password", "-a", name, "-s", "pickle"], null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == ItemNotFound)
        {
            return false;
        }

        return result.Success ? true : throw new SecretStoreException("security could not remove the secret: " + result.Message);
    }
}
