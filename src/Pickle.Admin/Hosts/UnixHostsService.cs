using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Admin.Hosts;

/// <summary>
/// /etc/hosts. Reading needs nothing; writing stages the validated file in a private temp folder and asks root to copy it
/// into place with <c>install</c>, so only that one command is ever privileged.
/// </summary>
public sealed class UnixHostsService(IPrivilegeService privilege, string? path = null, string? stagingDirectory = null) : IHostsService
{
    public string Path { get; } = path ?? "/etc/hosts";

    public async Task<HostsDocument> ReadAsync(CancellationToken cancellationToken = default) =>
        HostsDocument.Parse(File.Exists(Path) ? await File.ReadAllTextAsync(Path, cancellationToken).ConfigureAwait(false) : string.Empty);

    public async Task<ServiceOperationResult> WriteAsync(HostsDocument document, CancellationToken cancellationToken = default)
    {
        var text = document.Serialize();
        HostsDocument.Validate(text);
        var directory = StagingFolder.Create(stagingDirectory, "pickle-hosts-");
        var staged = System.IO.Path.Combine(directory.FullName, "hosts." + Guid.NewGuid().ToString("N")[..8]);
        await File.WriteAllTextAsync(staged, text, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var backup = new PrivilegedCommand("cp", ["-p", "--", Path, Path + ".pickle-backup"], "Back up the hosts file");
        var install = new PrivilegedCommand("install", ["-m", "0644", "-o", "root", "--", staged, Path], "Install the hosts file");
        if (File.Exists(Path))
        {
            var saved = await privilege.RunAsync(backup, cancellationToken).ConfigureAwait(false);
            if (!saved.Success)
            {
                return new ServiceOperationResult(false, saved.Message) { ShellCommand = saved.NeedsTerminal ? ShellScript(backup, install) : null };
            }
        }

        var result = await privilege.RunAsync(install, cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(result.Success, result.Success ? "The hosts file was saved (the previous one is hosts.pickle-backup)." : result.Message)
        {
            ShellCommand = result.NeedsTerminal ? ShellScript(install) : null,
        };
    }

    private string ShellScript(params PrivilegedCommand[] commands) => string.Join("; ", commands.Select(privilege.ShellCommand));
}
