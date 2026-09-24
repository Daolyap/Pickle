using System.Diagnostics;
using System.Management.Automation;
using Pickle.Abstractions;
using Pickle.Core.Contracts;
using Pickle.Core.Hosting;
using Pickle.Core.Translation;

namespace Pickle.Core.Profile;

/// <summary>
/// Sets <c>$PROFILE</c> (Pickle's profile.ps1, with the standard AllUsers*/CurrentUser* NoteProperties; CurrentUserAllHosts is
/// pwsh's shared profile.ps1) and runs profiles at startup: optionally pwsh's CurrentUserAllHosts and
/// Microsoft.PowerShell_profile.ps1 (Shell.LoadPwshProfile, with the PSReadLine shim loaded first), then Pickle's own
/// profile.ps1. Errors are reported and don't stop the remaining profiles. The first run writes a commented template.
/// </summary>
public sealed class ProfileLoader : IRuntimeComponent
{
    private const string TemplateMarker = "profile-template-created";
    private readonly PickleRuntime _runtime;

    public ProfileLoader(PickleRuntime runtime)
    {
        _runtime = runtime;
        ReadLine = new ReadLineCompat(runtime);
    }

    public ReadLineCompat ReadLine { get; }

    /// <summary>pwsh's per-user profile directory (Documents\PowerShell on Windows, ~/.config/powershell elsewhere).</summary>
    public string PwshProfileDirectory { get; set; } = DefaultPwshProfileDirectory();

    public string PwshCurrentUserAllHosts => Path.Combine(PwshProfileDirectory, "profile.ps1");

    public string PwshCurrentUserCurrentHost => Path.Combine(PwshProfileDirectory, "Microsoft.PowerShell_profile.ps1");

    public static string DefaultPwshProfileDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PowerShell");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var config = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config") : xdg;
        return Path.Combine(config, "powershell");
    }

    public void OnStarted() => SetProfileVariable();

    public void SetProfileVariable()
    {
        const string script = """
            param($pickle, $currentUserAllHosts)
            $value = [psobject]::new([string]$pickle)
            $value | Add-Member -NotePropertyName AllUsersAllHosts -NotePropertyValue (Join-Path $PSHOME 'profile.ps1')
            $value | Add-Member -NotePropertyName AllUsersCurrentHost -NotePropertyValue (Join-Path $PSHOME 'Pickle_profile.ps1')
            $value | Add-Member -NotePropertyName CurrentUserAllHosts -NotePropertyValue $currentUserAllHosts
            $value | Add-Member -NotePropertyName CurrentUserCurrentHost -NotePropertyValue ([string]$pickle)
            Set-Variable -Name PROFILE -Value $value -Scope Global -Force
            """;
        _runtime.Engine.InvokeSilently(script, new Dictionary<string, object?>
        {
            ["pickle"] = _runtime.Paths.ProfileFile,
            ["currentUserAllHosts"] = PwshCurrentUserAllHosts,
        });
    }

    public void Load()
    {
        EnsureTemplate();
        var total = Stopwatch.StartNew();
        if (_runtime.Config.Current.Shell.LoadPwshProfile)
        {
            ImportReadLineShim();
            Run(PwshCurrentUserAllHosts);
            Run(PwshCurrentUserCurrentHost);
        }

        Run(_runtime.Paths.ProfileFile);
        total.Stop();
        if (total.ElapsedMilliseconds > 1000)
        {
            _runtime.Terminal.Write(Ansi.Dim + $"Loading profiles took {total.ElapsedMilliseconds} ms." + Ansi.Reset + "\n");
        }
    }

    public void ImportReadLineShim()
    {
        var path = Path.Combine(EmbeddedModules.Extract(_runtime.Paths, _runtime.Log), "PSReadLine", "PSReadLine.psd1");
        _runtime.Engine.InvokeSilently("param($path) Import-Module -Name $path -Global -Force", new Dictionary<string, object?> { ["path"] = path });
    }

    private void Run(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var result = _runtime.Engine.ExecuteInteractive(". " + PowerShellText.SingleQuote(path));
            _runtime.Log.Info("profile", $"{path} ran in {sw.ElapsedMilliseconds} ms (success: {result.Success})");
        }
        catch (Exception ex) when (ex is RuntimeException or InvalidOperationException or IOException)
        {
            _runtime.Engine.Host.UI.WriteErrorLine($"Profile {path}: {ex.Message}");
            _runtime.Log.Error("profile", $"{path} failed", ex);
        }
    }

    private void EnsureTemplate()
    {
        var profile = _runtime.Paths.ProfileFile;
        var marker = Path.Combine(_runtime.Paths.DataDir, TemplateMarker);
        if (File.Exists(profile) || File.Exists(marker))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            File.WriteAllText(profile, Template);
            Directory.CreateDirectory(_runtime.Paths.DataDir);
            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _runtime.Log.Warn("profile", "Could not write the profile template", ex);
        }
    }

    public const string Template = """
        # Pickle profile: runs at startup like a PowerShell $PROFILE. Edit with `code $PROFILE` (or notepad/nano),
        # reload with `. $PROFILE`. Everything here is plain PowerShell. Uncomment what you like.

        # ── Aliases (saved in aliases.json and synced; `pk alias --help`) ─────────────────────────
        # pk alias add ll 'Get-ChildItem -Force'
        # pk alias add gco 'git checkout {branch}'                    # gco main
        # pk alias add serve 'python -m http.server {port=8000}'     # serve / serve 9000
        # Set-PickleAlias -Name proj -Body 'Set-Location ~/src/project' -Description 'jump to project'

        # ── Theme ───────────────────────────────────────────────────────────────────────────────
        # Pick a theme with "theme" in $PickleHome/config.json. Tweak syntax colors for this session:
        # Set-PSReadLineOption -Colors @{ Command = 'Green'; Parameter = 'DarkGray'; String = "`e[38;5;214m" }

        # ── Key bindings ────────────────────────────────────────────────────────────────────────
        # Set-PSReadLineKeyHandler -Chord Ctrl+f -Function ForwardWord
        # Set-PSReadLineKeyHandler -Chord UpArrow -Function HistorySearchBackward

        # ── A hook: run something whenever the directory changes ───────────────────────────────
        # $ExecutionContext.InvokeCommand.LocationChangedAction = {
        #     if (Test-Path .venv/Scripts/Activate.ps1) { . .venv/Scripts/Activate.ps1 }
        # }

        # ── Environment ─────────────────────────────────────────────────────────────────────────
        # $env:EDITOR = 'code --wait'
        """;
}
