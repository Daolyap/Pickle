using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Tui.Panels.Admin;

internal static class ResultOutput
{
    /// <summary>Prints the outcome of a privileged operation (and the line to run in the shell when it needs a password); returns the exit code.</summary>
    public static int Report(CommandOutput output, ServiceOperationResult result)
    {
        if (result.Success)
        {
            output.Success(result.Message);
            return 0;
        }

        output.Failure(result.Message);
        if (result.ShellCommand is { } command)
        {
            output.Muted("It needs a password. Run: " + command);
        }

        return 1;
    }
}
