using System.Globalization;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Winget;

/// <summary>Friendly messages for winget.exe exit codes (APPINSTALLER_CLI_ERROR_*) and Microsoft.WinGet.Client statuses.</summary>
internal static class WingetErrors
{
    private const uint RebootRequiredToFinish = 0x8A150109;
    private const uint RebootRequiredToInstall = 0x8A15010A;
    private const uint RebootInitiated = 0x8A15010B;

    private static readonly Dictionary<uint, string> Messages = new()
    {
        [0x8A150001] = "winget hit an internal error.",
        [0x8A150002] = "winget rejected the command-line arguments.",
        [0x8A150008] = "Downloading the installer failed. Check your network connection.",
        [0x8A15000F] = "The winget source data is missing or corrupt. Try 'pk winget repair-source'.",
        [0x8A150010] = "No installer is applicable to this system (architecture, scope or locale).",
        [0x8A150011] = "The installer hash doesn't match the manifest.",
        [0x8A150014] = "No package found matching the id.",
        [0x8A150016] = "Multiple packages match; use the exact id.",
        [0x8A15002B] = "No applicable upgrade found.",
        [0x8A150101] = "The application is currently running. Close it and try again.",
        [0x8A150102] = "Another installation is already in progress. Try again later.",
        [0x8A150103] = "One or more files are in use. Close the application and try again.",
        [0x8A150104] = "The package is missing a dependency on this system.",
        [0x8A150105] = "The disk is full.",
        [0x8A150106] = "Not enough memory to install.",
        [0x8A150107] = "The installer needs network connectivity.",
        [RebootRequiredToFinish] = "Installed. Restart your PC to finish the installation.",
        [RebootRequiredToInstall] = "A restart is required before this package can be installed.",
        [RebootInitiated] = "The installer initiated a restart.",
        [0x8A15010C] = "The installation was cancelled.",
        [0x8A15010D] = "Another version of this package is already installed.",
        [0x8A15010E] = "A newer version of this package is already installed.",
        [0x8A15010F] = "The installation was blocked by organization policy.",
    };

    public static WingetOperationResult FromExitCode(int exitCode, string output, string action, string id)
    {
        var code = unchecked((uint)exitCode);
        if (exitCode == 0)
        {
            return new WingetOperationResult(true, WingetCliParser.LastMessage(output) ?? $"{action} {id}: done.", 0);
        }

        var reboot = code is RebootRequiredToFinish or RebootRequiredToInstall or RebootInitiated;
        var success = code == RebootRequiredToFinish;
        var message = Messages.TryGetValue(code, out var known)
            ? known
            : WingetCliParser.LastMessage(output) ?? $"winget {action} failed.";
        return new WingetOperationResult(success, $"{action} {id}: {message} ({Hex(exitCode)})", exitCode, reboot);
    }

    /// <summary>Maps a Microsoft.WinGet.Client PSInstallResult/PSUninstallResult status.</summary>
    public static WingetOperationResult FromModuleStatus(string? status, bool rebootRequired, string action, string id, string? extendedError)
    {
        var ok = string.Equals(status, "Ok", StringComparison.OrdinalIgnoreCase);
        var message = status switch
        {
            "Ok" => rebootRequired ? "done. Restart your PC to finish." : "done.",
            "BlockedByPolicy" => "blocked by organization policy.",
            "CatalogError" => "the winget source failed. Try 'pk winget repair-source'.",
            "DownloadError" => "downloading the installer failed.",
            "InstallError" or "UninstallError" => "the installer reported an error.",
            "ManifestError" => "the package manifest is invalid.",
            "NoApplicableInstallers" => "no installer is applicable to this system.",
            "NoApplicableUpgrade" => "no applicable upgrade found.",
            "PackageAgreementsNotAccepted" => "package agreements were not accepted.",
            "InvalidOptions" => "invalid options.",
            "InternalError" => "winget hit an internal error.",
            null or "" => "no result returned.",
            _ => status + ".",
        };

        if (!ok && !string.IsNullOrEmpty(extendedError) && extendedError != "0")
        {
            message += $" ({extendedError})";
        }

        return new WingetOperationResult(ok, $"{action} {id}: {message}", ok ? 0 : 1, rebootRequired);
    }

    public static string Hex(int code) => "0x" + unchecked((uint)code).ToString("X8", CultureInfo.InvariantCulture);
}
