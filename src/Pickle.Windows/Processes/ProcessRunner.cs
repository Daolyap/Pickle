using System.Diagnostics;
using System.Text;
using Pickle.Abstractions;

namespace Pickle.Windows.Processes;

internal sealed record ProcessResult(int ExitCode, string Output, bool TimedOut = false);

/// <summary>Runs an external program with <see cref="ProcessStartInfo.ArgumentList"/> (never a command string).</summary>
internal interface IProcessRunner
{
    /// <param name="onSegment">Receives output split on CR and LF as it arrives (progress bars overwrite with CR).</param>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, Action<string>? onSegment, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class ProcessRunner(IPickleLogger log) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string>? onSegment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        log.Info("process", $"start {fileName} [{string.Join(", ", arguments)}]");
        using var process = new Process { StartInfo = psi };
        process.Start();
        process.StandardInput.Close();

        var output = new StringBuilder();
        var gate = new object();
        var stdout = PumpAsync(process.StandardOutput, output, gate, onSegment);
        var stderr = PumpAsync(process.StandardError, output, gate, onSegment);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            if (!timedOut)
            {
                log.Warn("process", $"cancelled {fileName}");
                throw;
            }
        }

        await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        var exitCode = timedOut ? -1 : process.ExitCode;
        log.Info("process", $"exit {fileName} code={exitCode} timedOut={timedOut}");
        string text;
        lock (gate)
        {
            text = output.ToString();
        }

        return new ProcessResult(exitCode, text, timedOut);
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder output, object gate, Action<string>? onSegment)
    {
        var buffer = new char[4096];
        var segment = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            lock (gate)
            {
                output.Append(buffer, 0, read);
            }

            if (onSegment is null)
            {
                continue;
            }

            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c is '\r' or '\n')
                {
                    if (segment.Length > 0)
                    {
                        onSegment(segment.ToString());
                        segment.Clear();
                    }
                }
                else
                {
                    segment.Append(c);
                }
            }
        }

        if (onSegment is not null && segment.Length > 0)
        {
            onSegment(segment.ToString());
        }
    }
}
