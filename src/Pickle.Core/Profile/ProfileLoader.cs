using System.Management.Automation;

namespace Pickle.Core.Profile;

/// <summary>
/// FOUNDATION PLACEHOLDER (workstream W4 completes this file): dot-sources Pickle's profile.ps1. W4 adds the
/// $PROFILE variable (with the standard NoteProperties), optional pwsh profile loading (Shell.LoadPwshProfile)
/// behind the PSReadLine shim, and timing/error reporting.
/// </summary>
public sealed class ProfileLoader
{
    private readonly PickleRuntime _runtime;

    public ProfileLoader(PickleRuntime runtime) => _runtime = runtime;

    public void Load()
    {
        var profile = _runtime.Paths.ProfileFile;
        if (!File.Exists(profile))
        {
            return;
        }

        try
        {
            _runtime.Engine.ExecuteInteractive(". '" + profile.Replace("'", "''", StringComparison.Ordinal) + "'");
        }
        catch (RuntimeException ex)
        {
            _runtime.Engine.Host.UI.WriteErrorLine($"Profile error: {ex.Message}");
        }
    }
}
