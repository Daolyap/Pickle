namespace Pickle.Abstractions.Services;

/// <summary>Where an on-demand tool install goes.</summary>
public enum ToolInstallScope
{
    /// <summary>For the current user only (no administrator rights needed for most packages).</summary>
    User,

    /// <summary>For every user of the machine (the installer asks for administrator rights).</summary>
    Machine,

    /// <summary>Into a throwaway folder for this session; uninstalled when Pickle exits (like a venv).</summary>
    Temporary,
}

/// <summary>
/// A command-line tool and the winget package that provides it. <paramref name="InstallDirs"/> are folders (environment
/// variables and a trailing <c>*</c> segment allowed) where the program lands when its installer doesn't add itself
/// to PATH, e.g. <c>%ProgramFiles%\7-Zip</c>.
/// </summary>
public sealed record ToolPackage(string Command, string WingetId, string Name, IReadOnlyList<string> InstallDirs)
{
    public ToolPackage(string command, string wingetId, string name, params string[] installDirs)
        : this(command, wingetId, name, (IReadOnlyList<string>)installDirs)
    {
    }
}

/// <summary>How to install a tool. <c>AddToPath</c> also adds its folder to the user's PATH permanently (the session always gets it).</summary>
public sealed record ToolInstallOptions(ToolInstallScope Scope = ToolInstallScope.User, bool AddToPath = true);

/// <summary>
/// Outcome of a tool install: <c>Executable</c> is where the command was found afterwards (if it was), <c>AddedToPath</c>
/// the folders added to PATH (the session's and, unless temporary or declined, the user's).
/// </summary>
public sealed record ToolInstallResult(bool Success, string Message, string? Executable = null, IReadOnlyList<string>? AddedToPath = null)
{
    public string? Output { get; init; }
}

/// <summary>
/// Installs command-line tools on demand: the "install it first?" prompt before a command whose program is missing,
/// <c>pk tool install</c> and the wizards' Install button. Windows implements it with winget.
/// </summary>
public interface IToolInstaller
{
    bool IsSupported { get; }

    /// <summary>The package that provides <paramref name="command"/> (<c>7z</c>, <c>nmap.exe</c>), or null when unknown.</summary>
    ToolPackage? Find(string command);

    Task<ToolInstallResult> InstallAsync(ToolPackage package, ToolInstallOptions options, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Packages installed with <see cref="ToolInstallScope.Temporary"/> in this session.</summary>
    IReadOnlyList<ToolPackage> TemporaryInstalls { get; }

    /// <summary>Uninstalls this session's temporary packages (Pickle calls it on exit).</summary>
    Task<ToolInstallResult> RemoveTemporaryAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Well-known tools and their winget packages; wizard definitions add more through their <c>wingetId</c>.</summary>
public static class ToolCatalog
{
    private static readonly ToolPackage[] Packages =
    [
        new("7z", "7zip.7zip", "7-Zip", @"%ProgramFiles%\7-Zip"),
        new("nmap", "Insecure.Nmap", "Nmap", @"%ProgramFiles(x86)%\Nmap", @"%ProgramFiles%\Nmap"),
        new("ncat", "Insecure.Nmap", "Nmap (ncat)", @"%ProgramFiles(x86)%\Nmap", @"%ProgramFiles%\Nmap"),
        new("tshark", "WiresharkFoundation.Wireshark", "Wireshark", @"%ProgramFiles%\Wireshark"),
        new("wireshark", "WiresharkFoundation.Wireshark", "Wireshark", @"%ProgramFiles%\Wireshark"),
        new("putty", "PuTTY.PuTTY", "PuTTY", @"%ProgramFiles%\PuTTY"),
        new("plink", "PuTTY.PuTTY", "PuTTY (plink)", @"%ProgramFiles%\PuTTY"),
        new("pscp", "PuTTY.PuTTY", "PuTTY (pscp)", @"%ProgramFiles%\PuTTY"),
        new("winscp", "WinSCP.WinSCP", "WinSCP", @"%ProgramFiles(x86)%\WinSCP", @"%LOCALAPPDATA%\Programs\WinSCP"),
        new("openssl", "ShiningLight.OpenSSL.Light", "OpenSSL", @"%ProgramFiles%\OpenSSL-Win64\bin"),
        new("wget", "JernejSimoncic.Wget", "GNU Wget"),
        new("jq", "jqlang.jq", "jq"),
        new("yq", "MikeFarah.yq", "yq"),
        new("gh", "GitHub.cli", "GitHub CLI", @"%ProgramFiles%\GitHub CLI"),
        new("git", "Git.Git", "Git", @"%ProgramFiles%\Git\cmd"),
        new("node", "OpenJS.NodeJS.LTS", "Node.js", @"%ProgramFiles%\nodejs"),
        new("npm", "OpenJS.NodeJS.LTS", "Node.js", @"%ProgramFiles%\nodejs"),
        new("python", "Python.Python.3.12", "Python 3.12", @"%LOCALAPPDATA%\Programs\Python\Python312", @"%ProgramFiles%\Python312"),
        new("python3", "Python.Python.3.12", "Python 3.12", @"%LOCALAPPDATA%\Programs\Python\Python312", @"%ProgramFiles%\Python312"),
        new("go", "GoLang.Go", "Go", @"%ProgramFiles%\Go\bin"),
        new("cargo", "Rustlang.Rustup", "Rust (rustup)", @"%USERPROFILE%\.cargo\bin"),
        new("rustup", "Rustlang.Rustup", "Rust (rustup)", @"%USERPROFILE%\.cargo\bin"),
        new("ffmpeg", "Gyan.FFmpeg", "FFmpeg"),
        new("ffprobe", "Gyan.FFmpeg", "FFmpeg"),
        new("yt-dlp", "yt-dlp.yt-dlp", "yt-dlp"),
        new("rg", "BurntSushi.ripgrep.MSVC", "ripgrep"),
        new("fd", "sharkdp.fd", "fd"),
        new("bat", "sharkdp.bat", "bat"),
        new("fzf", "junegunn.fzf", "fzf"),
        new("lazygit", "JesseDuffield.lazygit", "lazygit"),
        new("delta", "dandavison.delta", "delta"),
        new("kubectl", "Kubernetes.kubectl", "kubectl"),
        new("helm", "Helm.Helm", "Helm"),
        new("terraform", "Hashicorp.Terraform", "Terraform"),
        new("az", "Microsoft.AzureCLI", "Azure CLI", @"%ProgramFiles%\Microsoft SDKs\Azure\CLI2\wbin"),
        new("aws", "Amazon.AWSCLI", "AWS CLI", @"%ProgramFiles%\Amazon\AWSCLIV2"),
        new("docker", "Docker.DockerDesktop", "Docker Desktop", @"%ProgramFiles%\Docker\Docker\resources\bin"),
        new("code", "Microsoft.VisualStudioCode", "Visual Studio Code", @"%LOCALAPPDATA%\Programs\Microsoft VS Code\bin", @"%ProgramFiles%\Microsoft VS Code\bin"),
        new("vim", "vim.vim", "Vim", @"%ProgramFiles%\Vim\vim*"),
        new("nvim", "Neovim.Neovim", "Neovim", @"%ProgramFiles%\Neovim\bin"),
        new("pwsh", "Microsoft.PowerShell", "PowerShell 7", @"%ProgramFiles%\PowerShell\7"),
        new("cmake", "Kitware.CMake", "CMake", @"%ProgramFiles%\CMake\bin"),
        new("ninja", "Ninja-build.Ninja", "Ninja"),
        new("adb", "Google.PlatformTools", "Android platform tools"),
        new("fastboot", "Google.PlatformTools", "Android platform tools"),
        new("rclone", "Rclone.Rclone", "rclone"),
        new("restic", "restic.restic", "restic"),
        new("mkcert", "FiloSottile.mkcert", "mkcert"),
        new("speedtest", "Ookla.Speedtest.CLI", "Speedtest CLI"),

        // Modern replacements for everyday commands
        new("zoxide", "ajeetdsouza.zoxide", "zoxide"),
        new("eza", "eza-community.eza", "eza"),
        new("lsd", "lsd-rs.lsd", "lsd"),
        new("sd", "chmln.sd", "sd"),
        new("xh", "ducaale.xh", "xh"),
        new("dust", "bootandy.dust", "dust"),
        new("duf", "muesli.duf", "duf"),
        new("procs", "dalance.procs", "procs"),
        new("btm", "Clement.bottom", "bottom"),
        new("btop", "aristocratos.btop4win", "btop4win"),
        new("hexyl", "sharkdp.hexyl", "hexyl"),
        new("hyperfine", "sharkdp.hyperfine", "hyperfine"),
        new("tokei", "XAMPPRocky.Tokei", "tokei"),
        new("tldr", "tldr-pages.tlrc", "tldr (tlrc)"),
        new("glow", "charmbracelet.glow", "Glow"),
        new("gum", "charmbracelet.gum", "Gum"),
        new("just", "Casey.Just", "just"),
        new("make", "ezwinports.make", "GNU Make"),
        new("nano", "GNU.Nano", "GNU nano"),
        new("micro", "zyedidia.micro", "micro"),
        new("hx", "Helix.Helix", "Helix"),
        new("gsudo", "gerardog.gsudo", "gsudo", @"%ProgramFiles%\gsudo\Current"),
        new("starship", "Starship.Starship", "Starship", @"%ProgramFiles%\starship\bin"),
        new("oh-my-posh", "JanDeDobbeleer.OhMyPosh", "Oh My Posh", @"%LOCALAPPDATA%\Programs\oh-my-posh\bin"),

        // Networking
        new("gping", "orf.gping", "gping"),
        new("trip", "FujiApple.Trippy", "Trippy"),
        new("iperf3", "ar51an.iPerf3", "iPerf3"),
        new("croc", "schollz.croc", "croc"),
        new("ngrok", "Ngrok.Ngrok", "ngrok"),
        new("cloudflared", "Cloudflare.cloudflared", "cloudflared", @"%ProgramFiles(x86)%\cloudflared", @"%ProgramFiles%\cloudflared"),
        new("tailscale", "Tailscale.Tailscale", "Tailscale", @"%ProgramFiles%\Tailscale"),
        new("wg", "WireGuard.WireGuard", "WireGuard", @"%ProgramFiles%\WireGuard"),

        // Windows internals (Sysinternals)
        new("procmon", "Microsoft.Sysinternals.ProcessMonitor", "Process Monitor"),
        new("procexp", "Microsoft.Sysinternals.ProcessExplorer", "Process Explorer"),
        new("psexec", "Microsoft.Sysinternals.PsTools", "PsTools"),
        new("autoruns", "Microsoft.Sysinternals.Autoruns", "Autoruns"),
        new("tcpview", "Microsoft.Sysinternals.TCPView", "TCPView"),

        // Languages and developer tooling
        new("nvm", "CoreyButler.NVMforWindows", "NVM for Windows", @"%LOCALAPPDATA%\nvm", @"%APPDATA%\nvm"),
        new("pnpm", "pnpm.pnpm", "pnpm"),
        new("bun", "Oven-sh.Bun", "Bun", @"%USERPROFILE%\.bun\bin"),
        new("deno", "DenoLand.Deno", "Deno"),
        new("uv", "astral-sh.uv", "uv"),
        new("ruff", "astral-sh.ruff", "Ruff"),
        new("sqlite3", "SQLite.SQLite", "SQLite"),
        new("k9s", "Derailed.k9s", "k9s"),
        new("kubectx", "ahmetb.kubectx", "kubectx"),
        new("kubens", "ahmetb.kubectx", "kubectx (kubens)"),
        new("minikube", "Kubernetes.minikube", "minikube", @"%ProgramFiles%\Kubernetes\Minikube"),
        new("podman", "RedHat.Podman", "Podman", @"%ProgramFiles%\RedHat\Podman"),
        new("gcloud", "Google.CloudSDK", "Google Cloud CLI", @"%LOCALAPPDATA%\Google\Cloud SDK\google-cloud-sdk\bin", @"%ProgramFiles(x86)%\Google\Cloud SDK\google-cloud-sdk\bin"),
        new("ollama", "Ollama.Ollama", "Ollama", @"%LOCALAPPDATA%\Programs\Ollama"),

        // Files and media
        new("zstd", "Meta.Zstandard", "Zstandard"),
        new("magick", "ImageMagick.ImageMagick", "ImageMagick", @"%ProgramFiles%\ImageMagick-*"),
        new("pandoc", "JohnMacFarlane.Pandoc", "Pandoc", @"%LOCALAPPDATA%\Pandoc", @"%ProgramFiles%\Pandoc"),
        new("exiftool", "OliverBetz.ExifTool", "ExifTool", @"%LOCALAPPDATA%\Programs\ExifTool", @"%ProgramFiles%\ExifTool"),
    ];

    private static readonly Dictionary<string, ToolPackage> ByCommand =
        Packages.ToDictionary(p => p.Command, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ToolPackage> All => Packages;

    /// <summary>The known package for a command name (<c>7z</c>, <c>7z.exe</c>, <c>C:\x\7z.exe</c> → 7-Zip), or null.</summary>
    public static ToolPackage? Find(string command)
    {
        var name = NormalizeCommand(command);
        return name.Length > 0 && ByCommand.TryGetValue(name, out var package) ? package : null;
    }

    /// <summary>File name without directory or <c>.exe</c>.</summary>
    public static string NormalizeCommand(string command)
    {
        var name = Path.GetFileName(command.Trim());
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
