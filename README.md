<p align="center"><img src="assets/logo/pickle.svg" alt="Pickle logo" width="160"></p>

# Pickle

**A PowerShell 7 shell with superpowers.** Pickle runs the real PowerShell 7 engine — your cmdlets, modules,
scripts and profile all work — but replaces the interactive experience with something much nicer, and adds
first-class Windows tooling on top.

> Status: early development (0.x). Windows is the primary target; Linux and macOS work for the core shell.

## Highlights

| | |
|---|---|
| ✨ **Editor** | Live syntax highlighting, fish-style autosuggestions, multi-line editing, undo/redo, selection & clipboard |
| 🔎 **Search** | Fuzzy history (Ctrl+R) scoped to all / this directory / this session; fuzzy completion menu with descriptions |
| 🎨 **Prompt & themes** | Built-in themeable prompt (git, duration, status, k8s, aws/azure/gcloud, docker, venv, node, .NET/Go/Rust, battery…) and 18 themes — dracula, nord, gruvbox, catppuccin, tokyo-night, rose-pine, a light one, and **animated** aurora, synthwave, matrix, ember and prism; follows the system's light/dark mode (`pk theme auto`), imports any Windows Terminal color scheme (`pk theme import`), drives `$PSStyle` and your Windows Terminal colors; a red admin theme and logo when elevated ([themes guide](docs/themes.md)) |
| 📈 **Tab progress** | Windows Terminal's tab and taskbar button show a progress ring while a command runs, the real percentage for `Write-Progress`, and red when it failed; optional bell after long commands (`terminal.bellAfterSeconds`) |
| 🪟 **Panels** | Full-screen TUI panels: command palette (F1), files (Ctrl+T), git (Alt+G), jobs (Alt+J), processes (Alt+P), network (Alt+N), network tools (Alt+T), system dashboard (Alt+I), theme gallery (Alt+E), SSH hosts (Alt+H), disks with partitions (Alt+D), winget (Alt+W), Windows Update (Alt+U), Task Scheduler (Alt+S), Windows Sandbox (Alt+X), settings (Alt+,) |
| 🛰️ **Network tools** | Built in, nothing to install: port scan (`pk scan 10.0.0.0/24 -p top100`), host discovery with MACs (`pk sweep`), DNS lookups against any server (`pk dns`), `pk trace`, `pk whois`, TLS certificate checks (`pk cert`), `pk http` timings, `pk subnet`, Wake-on-LAN, `pk ip` |
| 📥 **Missing tools** | Type `7z …` or `nmap …` without them installed and Pickle offers to install first — just for you, all users or this session only, added to PATH — instead of a broken command (`pk tool install <name>`, ~100 known tools from zoxide to Sysinternals: `pk tool list`) |
| 📊 **System** | `pk dashboard` (Alt+I): OS, uptime, CPU/memory/swap history, disks, network, battery, top processes and pending restarts at a glance; `pk devious` fills the screen hacker-movie style with real data from your machine |
| 💽 **Disk management** | diskpart without the guesswork: the Disks panel's Partitions tab and wizards for Format-Volume, New/Resize/Remove-Partition, Initialize/Clear/Set-Disk, Repair- and Optimize-Volume; boot and system disks are never offered for wiping |
| 🔤 **Fonts** | Installs Cascadia Code Nerd Font on request (`pk font install`) and falls back to plain glyphs when the terminal's font lacks the icons, so no `�` in the prompt |
| 🧪 **Windows Sandbox** | Throwaway Windows in one key: presets (safe browsing, test an installer, offline analysis…), shared folders, networking and device switches, winget packages and Pickle itself inside (`pk sandbox run`) |
| 📦 **winget** | Browse, search (as you type), install and upgrade packages interactively — as administrator with one UAC prompt for the lot (`--admin`); `pk upgrade` updates apps *and* Windows; one-click (UAC) winget source repair |
| 🧙 **Command wizards** | F2 on a command opens a guided builder with live preview: curl, nmap, ffmpeg, git, docker, ssh/scp, openssl, robocopy, tar, 7z, kubectl, Get-WinEvent, netsh, certutil, adb, yt-dlp, rsync |
| 🐧 **Linux muscle memory** | `ls -la`, `grep -rn`, `rm -rf`, `export X=1`, `VAR=x cmd`, `2>/dev/null`, `!!`, `sudo`, `apt install` → sensible Windows equivalents (shown dimmed) |
| 🛡️ **Administrator** | A command that fails with "access denied" offers to run again as administrator (`sudo`, in an elevated Pickle — in Windows Terminal when you use it) |
| 🔗 **Aliases** | Permanent aliases, including parameterized (`gco {branch}`, filled in order or by name: `scan out=home 10.0.0.1`), script, directory- and machine-scoped |
| 🔌 **Plugins** | Any PowerShell module can be a plugin (`Register-PicklePanel`, `-Wizard`, `-Command`, `-PromptSegment`, `-Hook`…); .NET plugins add full panels and services — see the [plugin guide](docs/plugins.md) |
| ☁️ **Sync** | Sync config, aliases, themes and history across machines through a folder (OneDrive…) or a git repo |
| ⏰ **Scheduler** | `pk schedule add "every 30m" <command>` and a Task Scheduler panel |

## Install

| Method | Command |
|---|---|
| winget | `winget install Daolyap.Pickle` *(after the first release is published)* |
| Scoop | `scoop bucket add pickle https://github.com/Daolyap/Pickle` then `scoop install pickle/pickle` |
| MSI | Download `pickle-<version>-win-x64.msi` from [Releases](https://github.com/Daolyap/Pickle/releases) — choose the folder and features (PATH, Windows Terminal profile, Nerd Font, Start menu and desktop shortcuts); uninstalling asks whether to remove your settings and history too |
| Portable | Download `pickle-<version>-win-x64.exe` and run it; the first run offers the font, a Windows Terminal profile and making Pickle the default (`pk setup` shows it again) |
| Fedora / RHEL | Download `pickle-<version>-1.x86_64.rpm` from [Releases](https://github.com/Daolyap/Pickle/releases), then `sudo dnf install ./pickle-*.x86_64.rpm` |
| Other Linux | Download `pickle-<version>-linux-x64.tar.gz`, extract `pickle` somewhere on your `PATH` (needs `libicu`) |

Pickle looks for a newer release once a day in the background and says so under the banner
(`shell.checkForUpdates`); `pk version check` asks right away, and `pk version update` installs it for the portable exe and
the MSI (downloaded from the GitHub release and checked against its `SHA256SUMS.txt`). Scoop, winget and RPM installs
update through their package manager.

PowerShell 7 does **not** need to be installed — Pickle ships the engine. If `pwsh` is installed, its modules are
picked up too, and `pk config set shell.loadPwshProfile true` loads your existing profile.

## Quick tour

```powershell
pk help                      # all Pickle commands
pk theme set powerline       # switch theme (also updates the Windows Terminal color scheme)
pk theme set aurora          # an animated theme
pk theme gallery             # or Alt+E: every theme previewed live in its own colors
pk theme auto                # light theme in light mode, dark theme in dark mode
pk theme import "One Half Dark"   # any Windows Terminal color scheme as a Pickle theme
pk alias add gco 'git checkout {branch}' --kind param
pk alias add scan 'nmap -A -oN {out=scan}.txt {*}' --kind param   # then: scan out=home 10.0.0.0/24 10.0.0.9
pk winget search ripgrep     # or press Alt+W
pk upgrade                   # upgrade all apps + Windows updates
pk update check              # Windows Update
pk schedule add "daily 09:00" pk upgrade --name morning-upgrade
pk sync init "$env:OneDrive\Pickle"
pk config                    # or press Alt+, for the settings panel
pk scan 192.168.1.0/24 -p web      # port scan (Alt+T opens all the network tools)
pk dns example.com all             # every record type
pk tool install jq --temp          # a tool for this session only
pk dashboard                       # live system overview (Alt+I)
pk font install                    # Cascadia Code Nerd Font for the prompt icons
pk setup                           # run the first-start setup again
pk bugreport                       # a zip for an issue: versions, pk doctor, config and logs, secrets removed
pk sandbox run "Test an installer" # throwaway Windows with Downloads shared read-only
pk plugin new MyTools              # start a plugin (docs/plugins.md)
```

Keys: **F1** palette · **Ctrl+R** history · **Tab** completion · **→** accept suggestion · **Ctrl+T** files ·
**F2** wizard · **Alt+G** git · **Alt+W** winget · **Alt+U** updates · **Alt+J** jobs · **Alt+S** scheduler · **Alt+,** settings ·
**Alt+P** processes (`pk top`) · **Alt+N** network (`pk net`) · **Alt+T** network tools (`pk tools`) · **Alt+D** disks (`pk disks [folder]`) · **Alt+I** dashboard (`pk dashboard`) · **Alt+E** theme gallery (`pk theme gallery`) · **Alt+H** SSH hosts (`pk ssh`) ·
**Alt+X** Windows Sandbox (`pk sandbox`).

## Configuration

Everything lives in `%APPDATA%\Pickle` (`~/.config/pickle` elsewhere; override with `PICKLE_HOME`):
`config.json` (synced), `config.local.json` (this machine only), `aliases.json`, `themes/`, `wizards/`,
`plugins/`, `profile.ps1`, `history.jsonl`. `config.json` has a JSON schema, so editors autocomplete it.

## Development

C# / .NET 10, hosting `Microsoft.PowerShell.SDK`; Terminal.Gui v2 for panels. See [CLAUDE.md](CLAUDE.md) for the
conventions and testing approach, [docs/architecture.md](docs/architecture.md) for a map of the code, and
[docs/](docs/) for more.

```bash
scripts/check.sh            # build (warnings = errors) + format + all tests
scripts/check.sh --e2e      # plus real-terminal end-to-end tests
dotnet run --project src/Pickle
```

## License

[MIT](LICENSE)
