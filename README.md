# 🥒 Pickle

**A PowerShell 7 shell with superpowers.** Pickle runs the real PowerShell 7 engine — your cmdlets, modules,
scripts and profile all work — but replaces the interactive experience with something much nicer, and adds
first-class Windows tooling on top.

> Status: early development (0.x). Windows is the primary target; Linux and macOS work for the core shell.

## Highlights

| | |
|---|---|
| ✨ **Editor** | Live syntax highlighting, fish-style autosuggestions, multi-line editing, undo/redo, selection & clipboard |
| 🔎 **Search** | Fuzzy history (Ctrl+R) scoped to all / this directory / this session; fuzzy completion menu with descriptions |
| 🎨 **Prompt & themes** | Built-in themeable prompt (git, duration, status, k8s, venv, node…), 5 themes, drives `$PSStyle` and your Windows Terminal colors |
| 🪟 **Panels** | Full-screen TUI panels: command palette (F1), files (Ctrl+T), git (Alt+G), jobs (Alt+J), winget (Alt+W), Windows Update (Alt+U), Task Scheduler (Alt+S), settings (Alt+,) |
| 📦 **winget** | Browse, search, install and upgrade packages interactively; `pk upgrade` updates apps *and* Windows; one-click (UAC) winget source repair |
| 🧙 **Command wizards** | F2 on a command opens a guided builder with live preview: curl, nmap, ffmpeg, git, docker, ssh/scp, openssl, robocopy, tar, 7z, kubectl, Get-WinEvent, netsh, certutil, adb, yt-dlp, rsync |
| 🐧 **Linux muscle memory** | `ls -la`, `grep -rn`, `rm -rf`, `export X=1`, `VAR=x cmd`, `2>/dev/null`, `!!`, `sudo`, `apt install` → sensible Windows equivalents (shown dimmed) |
| 🔗 **Aliases** | Permanent aliases, including parameterized (`gco {branch}`), script, directory- and machine-scoped |
| 🔌 **Plugins** | Any PowerShell module can be a plugin (`Register-PicklePanel`, `-Command`, `-PromptSegment`, `-Hook`…); .NET plugins for deeper integration |
| ☁️ **Sync** | Sync config, aliases, themes and history across machines through a folder (OneDrive…) or a git repo |
| ⏰ **Scheduler** | `pk schedule add "every 30m" <command>` and a Task Scheduler panel |

## Install

| Method | Command |
|---|---|
| winget | `winget install Daolyap.Pickle` *(after the first release is published)* |
| Scoop | `scoop bucket add pickle https://github.com/daolyap/milkshell` then `scoop install pickle/pickle` |
| MSI | Download `pickle-<version>-win-x64.msi` from [Releases](https://github.com/daolyap/milkshell/releases) — adds Pickle to PATH, the Start menu and Windows Terminal |
| Portable | Download `pickle-<version>-win-x64.exe` and run it; it offers to add itself to Windows Terminal |

PowerShell 7 does **not** need to be installed — Pickle ships the engine. If `pwsh` is installed, its modules are
picked up too, and `pk config set shell.loadPwshProfile true` loads your existing profile.

## Quick tour

```powershell
pk help                      # all Pickle commands
pk theme set powerline       # switch theme (also updates the Windows Terminal color scheme)
pk alias add gco 'git checkout {branch}' --kind param
pk winget search ripgrep     # or press Alt+W
pk upgrade                   # upgrade all apps + Windows updates
pk update check              # Windows Update
pk schedule add "daily 09:00" pk upgrade --name morning-upgrade
pk sync init "$env:OneDrive\Pickle"
pk config                    # or press Alt+, for the settings panel
```

Keys: **F1** palette · **Ctrl+R** history · **Tab** completion · **→** accept suggestion · **Ctrl+T** files ·
**F2** wizard · **Alt+G** git · **Alt+W** winget · **Alt+U** updates · **Alt+J** jobs · **Alt+S** scheduler · **Alt+,** settings.

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
