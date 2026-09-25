using System.Diagnostics;
using System.Globalization;
using System.Management.Automation;
using Pickle.Abstractions;

namespace Pickle.Windows.Commands;

/// <summary>
/// Runs a long service call from a <c>pk</c> command with a live status line (spinner, elapsed time, the latest
/// progress detail), a timeout, and Ctrl+C: the command stops waiting as soon as its token is cancelled, even when
/// the work itself can't be aborted (a Windows Update Agent search, a hung winget).
/// </summary>
internal static class Busy
{
    private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(120);

    // Quick operations never show a status line.
    private static readonly TimeSpan ShowAfter = TimeSpan.FromMilliseconds(400);

    // How often a non-interactive session gets a "still working" line.
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(30);

    public static async Task<T> RunAsync<T>(
        CommandOutput output,
        string activity,
        Func<IProgress<string>, CancellationToken, Task<T>> work,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        string? detail = null;
        var progress = new SyncProgress<string>(d => Volatile.Write(ref detail, d));
        var clock = Stopwatch.StartNew();
        var interactive = output.Context.Interactive;
        var shown = false;
        var frame = 0;
        var nextHeartbeat = TimeSpan.Zero;

        Task<T> task;
        try
        {
            task = work(progress, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw TimedOut(activity, timeout);
        }

        var waiting = task.WaitAsync(cts.Token);
        try
        {
            while (!waiting.IsCompleted)
            {
                await Task.WhenAny(waiting, Task.Delay(Tick, CancellationToken.None)).ConfigureAwait(false);
                if (waiting.IsCompleted || clock.Elapsed < ShowAfter)
                {
                    continue;
                }

                var elapsed = CommandOutput.Elapsed(clock.Elapsed);
                var current = Volatile.Read(ref detail);
                if (interactive)
                {
                    var suffix = string.IsNullOrWhiteSpace(current) ? string.Empty : output.Dim("  " + Shorten(current, 48));
                    output.Status($"{output.Accent(Frames[frame++ % Frames.Length])} {activity}… {output.Dim(elapsed)}{suffix}  {output.Dim("Ctrl+C to cancel")}");
                    shown = true;
                }
                else if (clock.Elapsed >= nextHeartbeat)
                {
                    output.Muted(shown ? $"  … still {Lower(activity)} ({elapsed})" : $"{activity}…");
                    shown = true;
                    nextHeartbeat = clock.Elapsed + Heartbeat;
                }
            }

            var result = await waiting.ConfigureAwait(false);
            if (shown && interactive)
            {
                output.Muted($"{activity} — done in {CommandOutput.Elapsed(clock.Elapsed)}.");
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Observe(task);
            if (shown)
            {
                output.Muted($"{activity} — gave up after {CommandOutput.Elapsed(clock.Elapsed)}.");
            }

            throw TimedOut(activity, timeout);
        }
        catch (OperationCanceledException)
        {
            Observe(task);
            if (shown)
            {
                output.Muted($"{activity} — cancelled after {CommandOutput.Elapsed(clock.Elapsed)}.");
            }

            throw;
        }
        catch (Exception) when (shown)
        {
            output.Muted($"{activity} — failed after {CommandOutput.Elapsed(clock.Elapsed)}.");
            throw;
        }
    }

    public static TimeSpan ParseTimeout(string? value, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var text = value.Trim().ToLowerInvariant();
        var multiplier = 1;
        if (text.EndsWith('m'))
        {
            multiplier = 60;
            text = text[..^1];
        }
        else if (text.EndsWith('s'))
        {
            text = text[..^1];
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 && number <= 24 * 60
            ? TimeSpan.FromSeconds((long)number * multiplier)
            : throw new ArgumentException($"'{value}' is not a valid timeout (seconds, or e.g. 90s, 5m).");
    }

    private static TimeoutException TimedOut(string activity, TimeSpan timeout) =>
        new($"{activity} did not finish within {CommandOutput.Elapsed(timeout)} (use --timeout to wait longer).");

    // The abandoned task may still fail later; observe it so it doesn't surface as an unobserved exception.
    private static void Observe(Task task) => task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, System.Threading.Tasks.TaskScheduler.Default);

    private static string Shorten(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }

    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
