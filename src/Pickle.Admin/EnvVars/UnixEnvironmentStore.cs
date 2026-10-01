using System.Text;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Admin.EnvVars;

/// <summary>
/// Linux and macOS: Pickle's own scope (config.json) and, on Linux, <c>/etc/environment</c> as the machine scope
/// (<c>NAME=value</c> lines, no expansion), written through <see cref="IPrivilegeService"/> like the hosts file.
/// </summary>
public sealed class UnixEnvironmentStore(IConfigStore config, IPrivilegeService privilege, string? machineFile = null, string? stagingDirectory = null) : EnvironmentStoreBase(config)
{
    private readonly string _machineFile = machineFile ?? "/etc/environment";

    public override IReadOnlyList<EnvironmentScope> Scopes =>
        OperatingSystem.IsLinux() ? [EnvironmentScope.Pickle, EnvironmentScope.Machine, EnvironmentScope.Process] : [EnvironmentScope.Pickle, EnvironmentScope.Process];

    public override bool NeedsPrivileges(EnvironmentScope scope) => scope == EnvironmentScope.Machine;

    protected override Task<IReadOnlyList<EnvironmentVariable>> ListPersistentAsync(EnvironmentScope scope, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EnvironmentVariable>>(scope == EnvironmentScope.Machine && File.Exists(_machineFile)
            ? [.. Parse(File.ReadAllText(_machineFile)).Select(v => new EnvironmentVariable(v.Name, v.Value, scope))]
            : []);

    protected override async Task<ServiceOperationResult> SetPersistentAsync(EnvironmentScope scope, string name, string? value, CancellationToken cancellationToken)
    {
        if (scope != EnvironmentScope.Machine)
        {
            return new ServiceOperationResult(false, "Only Pickle's own and the machine-wide scopes exist on this OS.");
        }

        var current = File.Exists(_machineFile) ? await File.ReadAllTextAsync(_machineFile, cancellationToken).ConfigureAwait(false) : string.Empty;
        var updated = Apply(current, name, value);
        var directory = StagingFolder.Create(stagingDirectory, "pickle-env-");
        var staged = Path.Combine(directory.FullName, "environment." + Guid.NewGuid().ToString("N")[..8]);
        await File.WriteAllTextAsync(staged, updated, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var install = new PrivilegedCommand("install", ["-m", "0644", "-o", "root", "--", staged, _machineFile], "Install " + _machineFile);
        var result = await privilege.RunAsync(install, cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(result.Success, result.Success ? $"{name} saved for all users. New logins see it." : result.Message)
        {
            ShellCommand = result.NeedsTerminal ? privilege.ShellCommand(install) : null,
        };
    }

    public static IReadOnlyList<(string Name, string Value)> Parse(string text)
    {
        var list = new List<(string, string)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (line.Length == 0 || line[0] == '#' || eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && (value[0] == '"' || value[0] == '\''))
            {
                value = value[1..^1];
            }

            if (EnvironmentRules.IsValidName(key))
            {
                list.Add((key, value));
            }
        }

        return list;
    }

    /// <summary>The file with <paramref name="name"/> set (or removed when <paramref name="value"/> is null), every other line untouched.</summary>
    public static string Apply(string text, string name, string? value)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n').Where((l, i) => i > 0 || l.Length > 0).ToList();
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length > 0 && trimmed[0] != '#' && trimmed.StartsWith(name + "=", StringComparison.Ordinal))
            {
                if (replaced || value is null)
                {
                    lines.RemoveAt(i--);
                }
                else
                {
                    lines[i] = $"{name}=\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
                }

                replaced = true;
            }
        }

        if (!replaced && value is not null)
        {
            lines.Add($"{name}=\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"");
        }

        return lines.Count == 0 ? string.Empty : string.Join('\n', lines) + "\n";
    }
}
