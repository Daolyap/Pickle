namespace Pickle.Abstractions.Services;

public enum PrivilegeMethod
{
    /// <summary>No way to gain privileges was found (no sudo, pkexec or doas).</summary>
    None,

    /// <summary>Pickle already runs as root or administrator.</summary>
    AlreadyPrivileged,

    Sudo,
    Pkexec,
    Doas,
}

/// <summary>
/// One fixed program with its arguments that needs root. Pickle itself keeps running unprivileged; only this command is
/// elevated, and the user can always see it (<see cref="IPrivilegeService.ShellCommand"/>).
/// </summary>
public sealed record PrivilegedCommand(string Program, IReadOnlyList<string> Arguments, string Reason)
{
    public string? StandardInput { get; init; }
}

public sealed record PrivilegedResult(bool Success, string Message)
{
    /// <summary>It failed only because a password (or terminal) is needed: run <see cref="IPrivilegeService.ShellCommand"/> in the shell instead.</summary>
    public bool NeedsTerminal { get; init; }

    public string? Output { get; init; }
}

/// <summary>
/// Runs single commands as root on Linux and macOS (Windows uses <see cref="IElevationBroker"/> with an allowlist).
/// A panel first tries <see cref="RunAsync"/> (already root, <c>sudo -n</c>, or a polkit prompt); when that needs a
/// password on a terminal it hands <see cref="ShellCommand"/> to the shell, where sudo asks like it always does.
/// </summary>
public interface IPrivilegeService
{
    PrivilegeMethod Method { get; }

    bool IsPrivileged { get; }

    /// <summary>The command as PowerShell text with the privilege prefix, e.g. <c>sudo systemctl restart ssh</c>.</summary>
    string ShellCommand(PrivilegedCommand command);

    Task<PrivilegedResult> RunAsync(PrivilegedCommand command, CancellationToken cancellationToken = default);
}
