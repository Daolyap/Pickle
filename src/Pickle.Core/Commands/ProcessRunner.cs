using System.ComponentModel;
using System.Diagnostics;

namespace Pickle.Core.Commands;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    /// <summary>The most useful single line to show when the command failed.</summary>
    public string Message
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdErr;
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.LastOrDefault(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal))
                ?? lines.LastOrDefault()
                ?? $"exit code {ExitCode}";
        }
    }
}

/// <summary>Runs external programs with an argument list (never a shell string), captured output and a timeout.</summary>
public static class ProcessRunner
{
    public const int NotFound = -2;
    public const int TimedOut = -3;

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (ExecutableLocator.Find(fileName) is not { } executable)
        {
            return new ProcessResult(NotFound, string.Empty, $"{fileName} was not found on PATH.");
        }

        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string?>())
        {
            psi.Environment[key] = value;
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            return new ProcessResult(NotFound, string.Empty, $"{fileName} was not found: {ex.Message}");
        }

        if (process is null)
        {
            return new ProcessResult(NotFound, string.Empty, $"{fileName} could not be started");
        }

        using (process)
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new ProcessResult(TimedOut, await stdout.ConfigureAwait(false), $"{fileName} timed out");
            }

            return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
    }

    /// <summary>Environment for non-interactive git: never prompt on the terminal or pop up credential dialogs.</summary>
    public static IReadOnlyDictionary<string, string?> GitEnvironment(bool allowCredentialUi = false)
    {
        var env = new Dictionary<string, string?>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["LC_ALL"] = "C",
        };
        if (!allowCredentialUi)
        {
            env["GCM_INTERACTIVE"] = "never";
        }

        return env;
    }
}
