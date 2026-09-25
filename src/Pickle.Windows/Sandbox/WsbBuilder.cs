using System.Management.Automation.Language;
using System.Text;
using System.Xml.Linq;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Sandbox;

/// <summary>
/// Turns a <see cref="SandboxConfig"/> into a .wsb file and, for Pickle's customisations, a PowerShell setup script
/// that the sandbox runs at logon from a read-only shared folder.
/// </summary>
public static class WsbBuilder
{
    public const string SandboxDesktop = @"C:\Users\WDAGUtilityAccount\Desktop";
    public const string SetupMount = @"C:\PickleSandbox";
    public const string PickleMount = @"C:\Pickle";
    public const string SetupScriptName = "setup.ps1";

    public static bool NeedsSetup(SandboxConfig c) =>
        c.DarkMode || c.ShowFileExtensions || c.ShowHiddenFiles || InstallsWinget(c) || IncludesPickle(c)
        || !string.IsNullOrWhiteSpace(c.StartUrl) || (c.OpenMappedFolder && c.MappedFolders.Count > 0) || !string.IsNullOrWhiteSpace(c.SetupScript);

    public static bool InstallsWinget(SandboxConfig c) => c.InstallWinget || c.WingetPackages.Count > 0;

    public static bool IncludesPickle(SandboxConfig c) => c.IncludePickle || c.StartPickle;

    /// <summary>Problems that would make the sandbox fail or the setup pointless; empty when fine.</summary>
    public static IReadOnlyList<string> Validate(SandboxConfig c)
    {
        var errors = new List<string>();
        if (!SandboxNames.IsValid(c.Name))
        {
            errors.Add("Name: letters, digits, spaces, '-' and '_' only (at most 60).");
        }

        foreach (var folder in c.MappedFolders)
        {
            if (!Path.IsPathFullyQualified(folder.HostFolder))
            {
                errors.Add($"Shared folder '{folder.HostFolder}' must be a full path.");
            }

            if (folder.SandboxFolder is { Length: > 0 } inside && !Path.IsPathFullyQualified(inside) && !inside.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Sandbox folder '{inside}' must be a full path (C:\\…).");
            }
        }

        foreach (var id in c.WingetPackages.Where(id => !WindowsIds.IsValidWingetId(id)))
        {
            errors.Add($"'{id}' is not a winget package id (Publisher.Name).");
        }

        if (InstallsWinget(c) && c.Networking == SandboxSwitch.Disable)
        {
            errors.Add("Installing winget or packages needs networking.");
        }

        if (!string.IsNullOrWhiteSpace(c.StartUrl) && !Uri.TryCreate(c.StartUrl.Trim(), UriKind.Absolute, out _))
        {
            errors.Add($"Start page '{c.StartUrl}' is not an absolute URL.");
        }

        if (c.MemoryInMB is { } memory && memory < 2048)
        {
            errors.Add("Memory: at least 2048 MB.");
        }

        return errors;
    }

    /// <param name="setupFolder">Host folder holding setup.ps1 (shared read-only as C:\PickleSandbox).</param>
    /// <param name="pickleFolder">Host folder with pickle.exe, shared when the configuration includes Pickle.</param>
    public static string BuildXml(SandboxConfig c, string setupFolder, string? pickleFolder)
    {
        var root = new XElement("Configuration");
        AddSwitch(root, "VGpu", c.VGpu);
        AddSwitch(root, "Networking", c.Networking);

        var folders = c.MappedFolders.ToList();
        var setup = NeedsSetup(c);
        if (setup)
        {
            folders.Add(new SandboxMappedFolder { HostFolder = setupFolder, SandboxFolder = SetupMount, ReadOnly = true });
        }

        if (IncludesPickle(c) && pickleFolder is not null)
        {
            folders.Add(new SandboxMappedFolder { HostFolder = pickleFolder, SandboxFolder = PickleMount, ReadOnly = true });
        }

        if (folders.Count > 0)
        {
            root.Add(new XElement(
                "MappedFolders",
                folders.Select(f => new XElement(
                    "MappedFolder",
                    new XElement("HostFolder", f.HostFolder),
                    f.SandboxFolder is { Length: > 0 } inside ? new XElement("SandboxFolder", inside) : null,
                    new XElement("ReadOnly", f.ReadOnly ? "true" : "false")))));
        }

        var logon = setup
            ? $@"powershell.exe -NoProfile -ExecutionPolicy Bypass -File ""{SetupMount}\{SetupScriptName}"""
            : c.LogonCommand?.Trim();
        if (!string.IsNullOrEmpty(logon))
        {
            root.Add(new XElement("LogonCommand", new XElement("Command", logon)));
        }

        AddSwitch(root, "AudioInput", c.AudioInput);
        AddSwitch(root, "VideoInput", c.VideoInput);
        AddSwitch(root, "ProtectedClient", c.ProtectedClient);
        AddSwitch(root, "PrinterRedirection", c.PrinterRedirection);
        AddSwitch(root, "ClipboardRedirection", c.ClipboardRedirection);
        if (c.MemoryInMB is { } memory)
        {
            root.Add(new XElement("MemoryInMB", memory));
        }

        return root.ToString() + Environment.NewLine;
    }

    /// <summary>The logon script for Pickle's customisations (Windows PowerShell 5.1 inside the sandbox).</summary>
    public static string BuildSetupScript(SandboxConfig c)
    {
        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append("\r\n");

        Line($"# Pickle sandbox setup for {Comment(c.Name)}. Generated; edit the configuration in Pickle (Alt+X) instead.");
        Line("$ErrorActionPreference = 'Continue'");
        Line("$ProgressPreference = 'SilentlyContinue'");
        Line("Start-Transcript -Path (Join-Path $env:USERPROFILE 'Desktop\\pickle-setup.log') | Out-Null");
        Line("function Step([string]$Text, [scriptblock]$Body) {");
        Line("    Write-Host \"==> $Text\" -ForegroundColor Green");
        Line("    try { & $Body } catch { Write-Warning \"$Text failed: $_\" }");
        Line("}");

        var restartExplorer = false;
        if (c.DarkMode)
        {
            Line();
            Line("Step 'Dark mode' {");
            Line("    $key = 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize'");
            Line("    New-Item -Path $key -Force | Out-Null");
            Line("    Set-ItemProperty -Path $key -Name AppsUseLightTheme -Value 0 -Type DWord");
            Line("    Set-ItemProperty -Path $key -Name SystemUsesLightTheme -Value 0 -Type DWord");
            Line("}");
            restartExplorer = true;
        }

        if (c.ShowFileExtensions || c.ShowHiddenFiles)
        {
            Line();
            Line("Step 'Explorer options' {");
            Line("    $key = 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced'");
            if (c.ShowFileExtensions)
            {
                Line("    Set-ItemProperty -Path $key -Name HideFileExt -Value 0 -Type DWord");
            }

            if (c.ShowHiddenFiles)
            {
                Line("    Set-ItemProperty -Path $key -Name Hidden -Value 1 -Type DWord");
            }

            Line("}");
            restartExplorer = true;
        }

        if (restartExplorer)
        {
            Line("Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue");
        }

        if (IncludesPickle(c))
        {
            Line();
            Line($"Step 'Pickle on PATH' {{");
            Line($"    $env:Path += ';{PickleMount}'");
            Line($"    [Environment]::SetEnvironmentVariable('Path', [Environment]::GetEnvironmentVariable('Path', 'User') + ';{PickleMount}', 'User')");
            Line("}");
        }

        if (InstallsWinget(c))
        {
            Line();
            Line("Step 'Installing winget' {");
            Line("    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12");
            Line("    Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force | Out-Null");
            Line("    Set-PSRepository -Name PSGallery -InstallationPolicy Trusted");
            Line("    Install-Module -Name Microsoft.WinGet.Client -Force | Out-Null");
            Line("    Repair-WinGetPackageManager -Latest -Force | Out-Null");
            Line("}");
            foreach (var id in c.WingetPackages.Where(WindowsIds.IsValidWingetId).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Line($"Step {Quote("Installing " + id)} {{");
                Line($"    & (Join-Path $env:LOCALAPPDATA 'Microsoft\\WindowsApps\\winget.exe') install --id {Quote(id)} --exact --silent --accept-package-agreements --accept-source-agreements");
                Line("}");
            }
        }

        if (c.OpenMappedFolder && c.MappedFolders.FirstOrDefault() is { } first)
        {
            Line();
            Line($"Step 'Opening the shared folder' {{ Start-Process explorer.exe -ArgumentList {Quote(InsidePath(first))} }}");
        }

        if (!string.IsNullOrWhiteSpace(c.StartUrl))
        {
            Line();
            Line($"Step 'Opening the start page' {{ Start-Process msedge.exe -ArgumentList {Quote(c.StartUrl.Trim())} }}");
        }

        if (!string.IsNullOrWhiteSpace(c.SetupScript))
        {
            Line();
            Line("Step 'Your setup script' {");
            foreach (var scriptLine in c.SetupScript.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                Line("    " + scriptLine);
            }

            Line("}");
        }

        if (!string.IsNullOrWhiteSpace(c.LogonCommand))
        {
            Line();
            Line($"Step 'Logon command' {{ Start-Process cmd.exe -ArgumentList '/c', {Quote(c.LogonCommand.Trim())} }}");
        }

        if (c.StartPickle)
        {
            Line();
            Line($"Step 'Starting Pickle' {{ Start-Process '{PickleMount}\\pickle.exe' }}");
        }

        Line();
        Line("Write-Host '==> Sandbox ready' -ForegroundColor Green");
        Line("Stop-Transcript | Out-Null");
        return sb.ToString();
    }

    /// <summary>Where a shared folder appears inside the sandbox.</summary>
    public static string InsidePath(SandboxMappedFolder folder) =>
        folder.SandboxFolder is { Length: > 0 } inside ? inside : SandboxDesktop + "\\" + LastSegment(folder.HostFolder);

    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }

    private static void AddSwitch(XElement root, string name, SandboxSwitch value)
    {
        if (value != SandboxSwitch.Default)
        {
            root.Add(new XElement(name, value == SandboxSwitch.Enable ? "Enable" : "Disable"));
        }
    }

    private static string Quote(string value) => "'" + CodeGeneration.EscapeSingleQuotedStringContent(value) + "'";

    private static string Comment(string value) => string.Concat(value.Where(ch => !char.IsControl(ch)));
}

/// <summary>Names for saved sandbox configurations (also their file names).</summary>
public static class SandboxNames
{
    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 60 && name.Trim() == name
        && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is ' ' or '-' or '_');

    public static string FileName(string name) => name + ".json";
}
