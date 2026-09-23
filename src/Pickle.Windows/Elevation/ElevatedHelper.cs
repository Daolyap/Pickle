using Pickle.Abstractions;

namespace Pickle.Windows.Elevation;

/// <summary>
/// Entry point for `pickle.exe --elevated-helper &lt;pipe&gt; &lt;nonce&gt;` (launched with runas by the broker).
/// FOUNDATION PLACEHOLDER — workstream W8 implements the pipe server with the allowlisted operations.
/// </summary>
public static class ElevatedHelper
{
    public static int Run(string pipeName, string nonce, IPickleLogger log)
    {
        log.Error("elevation", "Elevated helper is not implemented yet.");
        return 1;
    }
}
