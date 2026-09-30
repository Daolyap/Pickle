using System.ComponentModel;
using System.Management.Automation;
using System.Runtime.InteropServices;
using System.Security;
using Pickle.Abstractions;
using Pickle.Core.Terminal;

namespace Pickle.Core.Hosting;

/// <summary>
/// After a command fails for lack of administrator rights, asks whether to run it again elevated (through the
/// <c>sudo</c> rewriter: a UAC prompt and an elevated Pickle window).
/// </summary>
internal static class ElevationOffer
{
    private const int AccessDenied = 5;
    private const int ElevationRequired = 740;
    private const int HResultAccessDenied = unchecked((int)0x80070005);
    private const int HResultElevationRequired = unchecked((int)0x800702E4);

    private static readonly string[] Phrases =
    [
        "access is denied", "access denied", "access to the path", "requires elevation", "requested operation requires elevation",
        "run as administrator", "administrator privileges", "administrative privileges", "administrator rights",
        "must be an administrator", "not have permission", "permission denied", "elevated",
    ];

    /// <summary>Whether a failed command looks like it needed administrator rights.</summary>
    public static bool IsPermissionFailure(ErrorRecord? error, int? exitCode)
    {
        if (error is null)
        {
            // A native program that failed without a PowerShell error: its exit code is all there is.
            return exitCode is AccessDenied or ElevationRequired;
        }

        if (error.CategoryInfo.Category == ErrorCategory.PermissionDenied
            || error.FullyQualifiedErrorId.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase)
            || error.FullyQualifiedErrorId.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        for (var ex = error.Exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is UnauthorizedAccessException or SecurityException
                || ex is Win32Exception { NativeErrorCode: AccessDenied or ElevationRequired }
                || ex.HResult is HResultAccessDenied or HResultElevationRequired
                || (ex is ExternalException external && external.ErrorCode is HResultAccessDenied or HResultElevationRequired))
            {
                return true;
            }
        }

        var message = error.ErrorDetails?.Message ?? error.Exception?.Message ?? string.Empty;
        return Phrases.Any(p => message.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The line to run elevated: sudo elevates one statement, so several run as one script block.</summary>
    public static string SudoLine(string line)
    {
        var trimmed = line.Trim();
        return Translation.ShellLexer.Statements(trimmed).Count > 1 ? "sudo & { " + trimmed + " }" : "sudo " + trimmed;
    }

    /// <summary>Asks on <paramref name="terminal"/>; true when the answer is yes.</summary>
    public static bool Ask(ITerminal terminal, Theme theme)
    {
        terminal.Write(Ansi.Colorize("⚡ That needs administrator rights. Run it as administrator? [y/N] ", theme.Ui.Warning));
        terminal.Flush();
        var key = terminal.ReadKey();
        var yes = key.Key == ConsoleKey.Y;
        terminal.Write((yes ? "y" : "n") + "\n");
        return yes;
    }
}
