using System.Globalization;

namespace Pickle.Core.Hosting;

/// <summary>
/// The terminal tab and taskbar progress indicator (OSC 9;4, Windows Terminal and ConEmu) for interactive commands: a
/// spinner while one runs, Write-Progress percentages, red after a failure until the next command. Also rings the bell
/// after a long command (<c>terminal.bellAfterSeconds</c>).
/// </summary>
public sealed class TabProgress(PickleRuntime runtime)
{
    private const int Clear = 0;
    private const int Normal = 1;
    private const int Error = 2;
    private const int Indeterminate = 3;

    private (int State, int Percent) _shown = (Clear, 0);
    private bool _running;

    /// <summary><c>terminal.tabProgress</c>: "on", "off", or "auto" (Windows Terminal or ConEmu, on an interactive terminal).</summary>
    public bool Enabled
    {
        get
        {
            var terminal = runtime.Terminal;
            if (!terminal.SupportsAnsi || !terminal.IsInteractive)
            {
                return false;
            }

            return runtime.Config.Current.Terminal.TabProgress?.Trim().ToLowerInvariant() switch
            {
                "on" or "true" or "always" => true,
                "off" or "false" or "never" => false,
                _ => IsSet("WT_SESSION") || IsSet("ConEmuPID"),
            };
        }
    }

    public void CommandStarted()
    {
        _running = true;
        Show(Indeterminate, 0);
    }

    public void CommandFinished(bool success, TimeSpan duration)
    {
        _running = false;
        Show(success ? Clear : Error, success ? 0 : 100);
        if (runtime.Config.Current.Terminal.BellAfterSeconds is > 0 and var seconds && duration.TotalSeconds >= seconds && runtime.Terminal.IsInteractive)
        {
            runtime.Terminal.Write("\a");
        }
    }

    /// <summary>Write-Progress of the running command: a percentage (0–100), or unknown (negative).</summary>
    public void Progress(int percent)
    {
        if (_running)
        {
            Show(percent >= 0 ? Normal : Indeterminate, Math.Clamp(percent, 0, 100));
        }
    }

    /// <summary>The command's progress records are all complete; it is still running.</summary>
    public void ProgressDone()
    {
        if (_running)
        {
            Show(Indeterminate, 0);
        }
    }

    /// <summary>Removes the indicator (Pickle is exiting).</summary>
    public void Reset()
    {
        _running = false;
        Show(Clear, 0);
    }

    public static string Sequence(int state, int percent) =>
        string.Create(CultureInfo.InvariantCulture, $"\u001b]9;4;{state};{percent}\u0007");

    private void Show(int state, int percent)
    {
        if (_shown == (state, percent) || (state != Clear && !Enabled) || (state == Clear && _shown.State == Clear))
        {
            return;
        }

        _shown = (state, percent);
        runtime.Terminal.Write(Sequence(state, percent));
    }

    private static bool IsSet(string variable) => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable));
}
