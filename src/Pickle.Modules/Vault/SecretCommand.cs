using System.Security;
using Pickle.Abstractions;

namespace Pickle.Modules.Vault;

/// <summary>Reads a line without echo. Null when there is no terminal to ask on.</summary>
public interface ISecretPrompt
{
    Task<string?> ReadAsync(string prompt, CancellationToken cancellationToken);
}

/// <summary>Asks through the shell's <c>Read-Host -AsSecureString</c> (masked by Pickle's line editor).</summary>
public sealed class ShellSecretPrompt(Func<IPickleShell> shellFactory) : ISecretPrompt
{
    private const string Script = "param($p) [System.Net.NetworkCredential]::new('', (Read-Host -Prompt $p -AsSecureString)).Password";

    public async Task<string?> ReadAsync(string prompt, CancellationToken cancellationToken)
    {
        var shell = shellFactory();
        if (!shell.IsInteractive)
        {
            return null;
        }

        var result = await shell.InvokeAsync(Script, new Dictionary<string, object?> { ["p"] = prompt }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Output.FirstOrDefault()?.BaseObject as string;
    }
}

/// <summary><c>pk secret</c>: keep API keys and tokens out of scripts, profiles and history.</summary>
internal sealed class SecretCommand(Func<VaultService> vaultFactory, ISecretPrompt prompt) : PickleCommandBase
{
    private const int MaxFileBytes = 64 * 1024;

    public override string Name => "secret";

    public override string Description => "Store API keys and tokens in the OS keychain or an encrypted file, and use them without typing them";

    public override string Usage => "pk secret set|get|list|remove|lock|status …   ·   pk secret run -e VARIABLE=secret [-e …] <command…>";

    public override IReadOnlyList<string> Examples =>
    [
        "pk secret set github-token                 (asks for the value; nothing is echoed)",
        "pk secret set deploy-key --file ~/.ssh/deploy.pem",
        "pk secret get github-token --plain         (without --plain you get a SecureString)",
        "pk secret run -e GH_TOKEN=github-token gh api user       (GH_TOKEN exists only while gh runs)",
        "pk secret run -e github-token curl -s https://example.com   (the variable defaults to GITHUB_TOKEN)",
        "pk secret list",
        "pk secret remove github-token",
    ];

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var sub = raw.Count == 0 ? "status" : raw[0].ToLowerInvariant();
        var rest = raw.Skip(1).ToList();
        return sub switch
        {
            "set" or "add" => await SetAsync(output, rest, cancellationToken).ConfigureAwait(false),
            "get" => await GetAsync(output, rest, cancellationToken).ConfigureAwait(false),
            "list" or "ls" => await ListAsync(output, cancellationToken).ConfigureAwait(false),
            "remove" or "rm" or "delete" => await RemoveAsync(output, rest, cancellationToken).ConfigureAwait(false),
            "run" => await RunSecretsAsync(output, rest, cancellationToken).ConfigureAwait(false),
            "lock" => Lock(output),
            "status" => await StatusAsync(output, cancellationToken).ConfigureAwait(false),
            _ => UsageError(output, $"Unknown secret command '{raw[0]}'."),
        };
    }

    private async Task<int> SetAsync(CommandOutput output, List<string> rest, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(rest, "value", "file");
        if (args.Error is { } error || args.Arg(0) is null)
        {
            return UsageError(output, args.Error ?? "Give the secret a name.");
        }

        var name = SecretName.Normalize(args.Arg(0));
        string? value;
        if (args.Value("file") is { } file)
        {
            value = ReadFile(file);
        }
        else if (args.Value("value") is { } given)
        {
            value = given;
            output.Warning("The value is now in your command history; leave out --value to be asked without an echo.");
        }
        else
        {
            value = await prompt.ReadAsync($"Value for {name}: ", cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                return UsageError(output, "There is no terminal to ask on; give --value or --file.");
            }
        }

        if (string.IsNullOrEmpty(value))
        {
            output.Failure("A secret cannot be empty.");
            return 1;
        }

        var vault = vaultFactory();
        var store = await vault.ActiveAsync(cancellationToken).ConfigureAwait(false);
        if (await vault.ExistsAsync(name, cancellationToken).ConfigureAwait(false)
            && !output.Confirm(args, $"'{name}' is already in {store.DisplayName}. Replace it?", defaultYes: false))
        {
            output.Muted("Left as it was.");
            return 1;
        }

        await vault.SetAsync(name, value, cancellationToken).ConfigureAwait(false);
        output.Success($"Saved '{name}' in {store.DisplayName}.");
        return 0;
    }

    private static string ReadFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new ArgumentException($"{path} does not exist.");
        }

        if (info.Length > MaxFileBytes)
        {
            throw new ArgumentException($"{path} is larger than {MaxFileBytes / 1024} KB; a secret should be a key or a token.");
        }

        var text = File.ReadAllText(path);
        return text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text.EndsWith('\n') ? text[..^1] : text;
    }

    private async Task<int> GetAsync(CommandOutput output, List<string> rest, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(rest);
        if (args.Arg(0) is null)
        {
            return UsageError(output, "Give the name of the secret.");
        }

        var name = SecretName.Normalize(args.Arg(0));
        if (await vaultFactory().GetAsync(name, cancellationToken).ConfigureAwait(false) is not { } value)
        {
            output.Failure($"There is no secret named '{name}'. See: pk secret list");
            return 1;
        }

        if (args.Has("plain"))
        {
            output.Object(value);
            return 0;
        }

        var secure = new SecureString();
        foreach (var c in value)
        {
            secure.AppendChar(c);
        }

        secure.MakeReadOnly();
        output.Object(secure);
        return 0;
    }

    private async Task<int> ListAsync(CommandOutput output, CancellationToken cancellationToken)
    {
        var vault = vaultFactory();
        var store = await vault.ActiveAsync(cancellationToken).ConfigureAwait(false);
        var secrets = await vault.ListAsync(cancellationToken).ConfigureAwait(false);
        if (secrets.Count == 0)
        {
            output.Muted($"No secrets in {store.DisplayName}. Add one: pk secret set <name>");
            return 0;
        }

        foreach (var secret in secrets)
        {
            output.Object(Display.Columns(new SecretRow(secret.Name, secret.Updated, store.DisplayName), "Name", "Updated", "Store"));
        }

        return 0;
    }

    private async Task<int> RemoveAsync(CommandOutput output, List<string> rest, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(rest);
        if (args.Arg(0) is null)
        {
            return UsageError(output, "Give the name of the secret.");
        }

        var name = SecretName.Normalize(args.Arg(0));
        var vault = vaultFactory();
        var store = await vault.ActiveAsync(cancellationToken).ConfigureAwait(false);
        if (!output.Confirm(args, $"Remove '{name}' from {store.DisplayName}?", defaultYes: false))
        {
            output.Muted("Left as it was.");
            return 1;
        }

        if (!await vault.RemoveAsync(name, cancellationToken).ConfigureAwait(false))
        {
            output.Failure($"There is no secret named '{name}'.");
            return 1;
        }

        output.Success($"Removed '{name}'.");
        return 0;
    }

    private async Task<int> RunSecretsAsync(CommandOutput output, List<string> rest, CancellationToken cancellationToken)
    {
        // PowerShell eats a bare `--` before pk sees it, so the command simply starts at the first word that is not an -e option.
        var wanted = new List<(string Name, string Variable)>();
        var index = 0;
        while (index < rest.Count)
        {
            var word = rest[index];
            string spec;
            if (word is "-e" or "--env")
            {
                spec = ++index < rest.Count ? rest[index] : throw new ArgumentException($"{word} needs a value: -e VARIABLE=secret-name");
            }
            else if (word.StartsWith("--env=", StringComparison.Ordinal))
            {
                spec = word["--env=".Length..];
            }
            else
            {
                if (word == "--")
                {
                    index++;
                }

                break;
            }

            index++;
            var eq = spec.IndexOf('=', StringComparison.Ordinal);
            var name = SecretName.Normalize(eq < 0 ? spec : spec[(eq + 1)..]);
            var variable = SecretName.RequireVariable(eq < 0 ? SecretName.DefaultVariable(name) : spec[..eq]);
            if (wanted.Any(w => string.Equals(w.Variable, variable, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"{variable} would be set twice.");
            }

            wanted.Add((name, variable));
        }

        if (wanted.Count == 0 || index >= rest.Count)
        {
            return UsageError(output, "Name the secrets, then the command: pk secret run -e GH_TOKEN=github-token gh api user");
        }

        var vault = vaultFactory();
        var values = new List<(string Variable, string Value)>();
        foreach (var (name, variable) in wanted)
        {
            if (await vault.GetAsync(name, cancellationToken).ConfigureAwait(false) is not { } value)
            {
                output.Failure($"There is no secret named '{name}'. See: pk secret list");
                return 1;
            }

            values.Add((variable, value));
        }

        var command = rest.Skip(index).ToList();
        foreach (var (variable, value) in values)
        {
            Environment.SetEnvironmentVariable(variable, value);
        }

        var variables = values.Select(v => v.Variable).ToList();
        var shell = output.Pickle.Shell;
        if (shell.IsInteractive)
        {
            // Queued so the program owns the terminal; the line removes the variables again when it ends.
            shell.SubmitCommand(RunLine(command, variables));
            return 0;
        }

        try
        {
            var result = await shell.InvokeAsync(
                "param($program, $arguments) & $program @arguments",
                new Dictionary<string, object?> { ["program"] = command[0], ["arguments"] = command.Skip(1).ToArray() },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var item in result.Output)
            {
                output.Object(item);
            }

            foreach (var problem in result.Errors)
            {
                output.Context.WriteError(problem.ToString());
            }

            return result.HadErrors ? 1 : 0;
        }
        finally
        {
            foreach (var variable in variables)
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
        }
    }

    /// <summary><c>try { &amp; program args } finally { Remove-Item Env:A,Env:B }</c> with every value quoted; flags stay bare so cmdlet parameters still bind.</summary>
    internal static string RunLine(IReadOnlyList<string> command, IReadOnlyList<string> variables)
    {
        var words = command.Select((word, index) => index > 0 && IsFlag(word) ? word : PowerShellQuote.Single(word));
        var cleanup = string.Join(',', variables.Select(v => "Env:" + v));
        return $"try {{ & {string.Join(' ', words)} }} finally {{ Remove-Item {cleanup} -ErrorAction SilentlyContinue }}";
    }

    private static bool IsFlag(string word) =>
        word.Length > 1 && word[0] == '-' && word.TrimStart('-') is { Length: > 0 } name && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private int Lock(CommandOutput output)
    {
        vaultFactory().Lock();
        output.Success("Locked: the encrypted file asks for its password again.");
        return 0;
    }

    private async Task<int> StatusAsync(CommandOutput output, CancellationToken cancellationToken)
    {
        var vault = vaultFactory();
        try
        {
            var active = await vault.ActiveAsync(cancellationToken).ConfigureAwait(false);
            var count = (await vault.ListAsync(cancellationToken).ConfigureAwait(false)).Count;
            output.Line($"Secrets are kept in {output.Accent(active.DisplayName)} ({count} stored).");
        }
        catch (SecretStoreException ex)
        {
            output.Failure(ex.Message);
        }

        foreach (var store in vault.Stores)
        {
            var available = await store.IsAvailableAsync(cancellationToken).ConfigureAwait(false);
            output.Muted($"  {store.Id,-20} {store.DisplayName}{(available ? string.Empty : " (not available here)")}");
        }

        output.Muted("Choose one: pk config set extensions.vault.backend <auto|" + string.Join('|', vault.Stores.Select(s => s.Id)) + ">");
        return 0;
    }

    private sealed record SecretRow(string Name, DateTimeOffset? Updated, string Store);
}
