using System.Runtime.Versioning;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Modules.Vault;

/// <summary>API keys and tokens in the OS credential store (or an encrypted file): <c>pk secret set|get|run</c>.</summary>
public sealed class VaultModule : IPicklePlugin
{
    public const string ModuleId = "vault";

    public static ModuleDescriptor Descriptor { get; } = new()
    {
        Id = ModuleId,
        Name = "Secrets vault",
        Description = "API keys and tokens in Credential Manager / the Secret Service / Keychain, or an encrypted file: pk secret set, get, run",
        Create = () => new VaultModule(),
        Tools = ["secret-tool (Linux)", "security (macOS)"],
        Provides = ["pk secret"],
    };

    string IPicklePlugin.Id => "pickle." + ModuleId;

    public string DisplayName => Descriptor.Name;

    public string Description => Descriptor.Description;

    public void Initialize(IPickleContext context)
    {
        var directory = Path.Combine(context.Paths.DataDir, "vault");
        var prompt = new ShellSecretPrompt(() => context.Shell);
        Func<VaultSettings> settings = () => context.Config.Get<VaultSettings>(ModuleId);
        Func<IProgramRunner> runner = () => context.Services.Require<IProgramRunner>();

        var stores = new List<ISecretStore>();
        if (OperatingSystem.IsWindows())
        {
            stores.Add(CreateCredentialStore());
        }
        else if (OperatingSystem.IsMacOS())
        {
            stores.Add(new KeychainStore(runner));
        }
        else
        {
            stores.Add(new LibSecretStore(runner));
        }

        stores.Add(new EncryptedFileStore(
            Path.Combine(directory, "secrets.vault"),
            prompt.ReadAsync,
            () => TimeSpan.FromMinutes(Math.Max(0, settings().UnlockMinutes))));

        var vault = new VaultService(settings, stores, new SecretIndex(Path.Combine(directory, "index.json")));
        context.Services.Add(vault);
        context.Commands.Register(new SecretCommand(() => vault, prompt));
    }

    [SupportedOSPlatform("windows")]
    private static WindowsCredentialStore CreateCredentialStore() => new();
}
