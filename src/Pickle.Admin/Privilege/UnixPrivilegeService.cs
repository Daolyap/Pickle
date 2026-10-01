using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Admin.Privilege;

/// <summary>
/// <see cref="IPrivilegeService"/> for Linux and macOS. Order of preference for running without a terminal: already root,
/// a polkit prompt (<c>pkexec</c>, only in a graphical session), then <c>sudo -n</c> (works with NOPASSWD or a cached
/// ticket). A command that needs a password reports <see cref="PrivilegedResult.NeedsTerminal"/>.
/// </summary>
public sealed class UnixPrivilegeService(IProgramRunner runner, Func<bool>? isRoot = null, Func<string, string?>? environment = null) : IPrivilegeService
{
    private readonly Func<bool> _isRoot = isRoot ?? (() => Environment.IsPrivilegedProcess);
    private readonly Func<string, string?> _env = environment ?? Environment.GetEnvironmentVariable;

    public bool IsPrivileged => _isRoot();

    public PrivilegeMethod Method =>
        IsPrivileged ? PrivilegeMethod.AlreadyPrivileged
        : runner.Find("sudo") is not null ? PrivilegeMethod.Sudo
        : runner.Find("doas") is not null ? PrivilegeMethod.Doas
        : runner.Find("pkexec") is not null ? PrivilegeMethod.Pkexec
        : PrivilegeMethod.None;

    public string ShellCommand(PrivilegedCommand command)
    {
        var plain = PowerShellQuote.Command(command.Program, command.Arguments);
        return Method switch
        {
            PrivilegeMethod.AlreadyPrivileged => plain,
            PrivilegeMethod.Doas => "doas " + plain,
            PrivilegeMethod.Pkexec => "pkexec " + plain,
            _ => "sudo " + plain,
        };
    }

    public async Task<PrivilegedResult> RunAsync(PrivilegedCommand command, CancellationToken cancellationToken = default)
    {
        if (runner.Find(command.Program) is not { } program)
        {
            return new PrivilegedResult(false, $"{command.Program} was not found on PATH.");
        }

        var options = new ProgramRunOptions { StandardInput = command.StandardInput, Timeout = TimeSpan.FromMinutes(10) };
        (string Program, IEnumerable<string> Args)? launch = null;
        if (IsPrivileged)
        {
            launch = (program, command.Arguments);
        }
        else if (HasGraphicalSession() && runner.Find("pkexec") is { } pkexec)
        {
            launch = (pkexec, [program, .. command.Arguments]);
        }
        else if (runner.Find("sudo") is { } sudo)
        {
            launch = (sudo, ["-n", program, .. command.Arguments]);
        }
        else if (runner.Find("doas") is { } doas)
        {
            launch = (doas, ["-n", program, .. command.Arguments]);
        }

        if (launch is not { } run)
        {
            return new PrivilegedResult(false, "Nothing to gain root with: install sudo, doas or polkit (pkexec), or run Pickle as root.") { NeedsTerminal = false };
        }

        var result = await runner.RunAsync(run.Program, run.Args, options, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            return new PrivilegedResult(true, command.Reason + " done.") { Output = result.StdOut };
        }

        var text = result.StdErr + result.StdOut;
        var needsPassword = text.Contains("a password is required", StringComparison.OrdinalIgnoreCase)
            || text.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no tty present", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Authentication is needed", StringComparison.OrdinalIgnoreCase)
            || (run.Program.EndsWith("doas", StringComparison.Ordinal) && result.ExitCode == 1 && text.Contains("authentication failed", StringComparison.OrdinalIgnoreCase));
        if (result.ExitCode == 126 && run.Program.EndsWith("pkexec", StringComparison.Ordinal))
        {
            return new PrivilegedResult(false, "Authentication was dismissed.");
        }

        return new PrivilegedResult(false, needsPassword ? "A password is needed." : result.Message) { NeedsTerminal = needsPassword, Output = result.StdOut };
    }

    private bool HasGraphicalSession() => !string.IsNullOrEmpty(_env("DISPLAY")) || !string.IsNullOrEmpty(_env("WAYLAND_DISPLAY"));
}
