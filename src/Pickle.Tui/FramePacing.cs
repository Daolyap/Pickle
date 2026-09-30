using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Terminal.Gui.App;

namespace Pickle.Tui;

/// <summary>
/// How often a panel handles input and redraws. Terminal.Gui caps its main loop at 25 iterations a second, so a wheel
/// notch or key could wait 40 ms for its frame and scrolling moved in visible steps. Windows rounds every sleep (the
/// loop's and the input thread's 20 ms poll) up to its 15.6 ms timer tick, which made that worse; while a panel is
/// open the process asks for a 1 ms timer.
/// </summary>
internal static partial class FramePacing
{
    public const ushort FramesPerSecond = 60;

    public static IDisposable Begin()
    {
        Application.MaximumIterationsPerSecond = FramesPerSecond;
        return OperatingSystem.IsWindows() ? new FineTimer() : NoOp.Instance;
    }

    [SupportedOSPlatform("windows")]
    private sealed partial class FineTimer : IDisposable
    {
        private bool _active = TimeBeginPeriod(1) == 0;

        public void Dispose()
        {
            if (_active)
            {
                _active = false;
                _ = TimeEndPeriod(1);
            }
        }

        [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static partial uint TimeBeginPeriod(uint milliseconds);

        [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static partial uint TimeEndPeriod(uint milliseconds);
    }

    private sealed class NoOp : IDisposable
    {
        public static readonly NoOp Instance = new();

        public void Dispose()
        {
        }
    }
}
