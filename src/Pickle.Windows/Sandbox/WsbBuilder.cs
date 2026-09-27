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

    // Windows Sandbox has no winget. Repair-WinGetPackageManager fails there (winget-cli#5559: it cannot resolve
    // Microsoft.UI.Xaml), which left packages uninstallable until the sandbox was restarted. This follows winget-pkgs'
    // Tools/SandboxTest.ps1 instead: install the release's dependency .appx files and msixbundle (SHA-256 checked
    // against the release's .txt files), and call winget.exe by its resolved path because the WindowsApps alias
    // can appear only after a delay.
    private static readonly string WingetBootstrap = """
        function Update-PickleEnvironment {
            $paths = foreach ($scope in 'Machine', 'User') { [Environment]::GetEnvironmentVariable('Path', $scope) -split ';' }
            $env:Path = (@($env:Path -split ';') + $paths | Where-Object { $_ } | Select-Object -Unique) -join ';'
        }

        function Find-PickleWinget {
            $alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
            if (Test-Path $alias) { return $alias }
            $package = Get-AppxPackage -Name Microsoft.DesktopAppInstaller -ErrorAction SilentlyContinue | Sort-Object Version -Descending | Select-Object -First 1
            if ($package -and (Test-Path (Join-Path $package.InstallLocation 'winget.exe'))) { return Join-Path $package.InstallLocation 'winget.exe' }
            return $null
        }

        function Save-PickleReleaseAsset($Release, [string]$Name, [string]$Folder) {
            $asset = $Release.assets | Where-Object { $_.name -eq $Name } | Select-Object -First 1
            if (-not $asset) { throw "$Name is not part of winget $($Release.tag_name)" }
            $file = Join-Path $Folder $Name
            Invoke-WebRequest -UseBasicParsing -Uri $asset.browser_download_url -OutFile $file
            $hashAsset = $Release.assets | Where-Object { $_.name -eq ([IO.Path]::GetFileNameWithoutExtension($Name) + '.txt') } | Select-Object -First 1
            if ($hashAsset) {
                $hashFile = Join-Path $Folder $hashAsset.name
                Invoke-WebRequest -UseBasicParsing -Uri $hashAsset.browser_download_url -OutFile $hashFile
                $expected = [regex]::Match((Get-Content -Raw -Path $hashFile), '[0-9A-Fa-f]{64}').Value
                $actual = (Get-FileHash -Algorithm SHA256 -Path $file).Hash
                if (-not $expected -or $actual -ne $expected) { throw "$Name failed its SHA-256 check" }
            }
            return $file
        }

        function Install-PickleWinget {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            if (-not (Find-PickleWinget)) {
                try {
                    $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
                    $work = Join-Path $env:TEMP 'pickle-winget'
                    New-Item -ItemType Directory -Force -Path $work | Out-Null
                    $release = Invoke-RestMethod -UseBasicParsing -Uri 'https://api.github.com/repos/microsoft/winget-cli/releases/latest' -Headers @{ 'User-Agent' = 'Pickle-Sandbox' }
                    $dependencies = Save-PickleReleaseAsset $release 'DesktopAppInstaller_Dependencies.zip' $work
                    $bundle = Save-PickleReleaseAsset $release 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle' $work
                    Expand-Archive -Path $dependencies -DestinationPath (Join-Path $work 'dependencies') -Force
                    foreach ($appx in Get-ChildItem -Path (Join-Path $work 'dependencies') -Recurse -Filter '*.appx' | Where-Object { $_.FullName -match $arch }) {
                        try { Add-AppxPackage -Path $appx.FullName -ErrorAction Stop } catch { Write-Warning "$($appx.Name): $_" }
                    }
                    Add-AppxPackage -Path $bundle -ErrorAction Stop
                } catch {
                    Write-Warning "Installing winget from its GitHub release failed ($_); trying Repair-WinGetPackageManager."
                    Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force | Out-Null
                    Set-PSRepository -Name PSGallery -InstallationPolicy Trusted
                    Install-Module -Name Microsoft.WinGet.Client -Force | Out-Null
                    Repair-WinGetPackageManager -Latest -Force | Out-Null
                }
            }

            Update-PickleEnvironment
            for ($i = 0; $i -lt 30 -and -not (Find-PickleWinget); $i++) { Start-Sleep -Seconds 1 }
            $script:Winget = Find-PickleWinget
            if (-not $script:Winget) { throw 'winget.exe did not appear after installing App Installer.' }
            Set-Alias -Name winget -Value $script:Winget -Scope Script
            & $script:Winget --version
        }

        function Disable-PickleSlowMsiCheck {
            # MSI installers crawl in the sandbox while Smart App Control's reputation check is on (Windows-Sandbox#68).
            reg.exe add 'HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy' /v VerifiedAndReputablePolicyState /t REG_DWORD /d 0 /f | Out-Null
            if (Get-Command CiTool.exe -ErrorAction SilentlyContinue) { CiTool.exe --refresh --json | Out-Null }
        }

        function Install-PickleWingetPackage([string]$Id) {
            if (-not $script:Winget) { throw 'winget is not available.' }
            for ($attempt = 1; $attempt -le 3; $attempt++) {
                & $script:Winget install --id $Id --exact --silent --source winget --accept-package-agreements --accept-source-agreements --disable-interactivity
                # 0x8A150061 already installed, 0x8A15002B no applicable upgrade.
                if ($LASTEXITCODE -in 0, -1978335135, -1978335189) { Update-PickleEnvironment; return }
                Write-Warning "winget exited with $LASTEXITCODE (attempt $attempt of 3)."
                if ($attempt -eq 1) { & $script:Winget source reset --force | Out-Null; & $script:Winget source update | Out-Null }
                Start-Sleep -Seconds 5
            }
            throw "winget could not install $Id."
        }

        """.ReplaceLineEndings("\r\n");

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
            sb.Append(WingetBootstrap);
            Line("Step 'Installing winget' { Install-PickleWinget }");
            var packages = c.WingetPackages.Where(WindowsIds.IsValidWingetId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (packages.Count > 0)
            {
                Line("Step 'Speeding up MSI installers' { Disable-PickleSlowMsiCheck }");
            }

            foreach (var id in packages)
            {
                Line($"Step {Quote("Installing " + id)} {{ Install-PickleWingetPackage {Quote(id)} }}");
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
