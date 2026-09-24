# Manual test checklist (Windows)

Automated tests cover the engine, editor, parsers and panels (with fakes) on Linux and Windows CI. These checks
need a real Windows desktop: UAC, winget, Windows Update, Task Scheduler and Windows Terminal. Tick them off
after installing a build.

## Install

- [ ] **MSI**: `pickle-<ver>-win-x64.msi` installs without errors; `pickle --version` works in a *new* terminal
      (PATH updated); Start menu has "Pickle".
- [ ] **Portable**: `pickle.exe` runs from any folder; first run offers to add the Windows Terminal profile.
- [ ] **winget / Scoop** (after publishing): `winget install Daolyap.Pickle`, `scoop install pickle/pickle`.
- [ ] Uninstall (Apps & features) removes the exe, PATH entry, Start menu item and the Terminal fragment.

## Windows Terminal

- [ ] Windows Terminal shows a **Pickle** profile (Settings → Profiles) with the Pickle color scheme.
- [ ] `pk terminal set font "Cascadia Code NF"` / `pk terminal set opacity 80` update the profile live.
- [ ] `pk theme set powerline` changes the prompt *and* the Terminal color scheme.
- [ ] `pk terminal default` makes Pickle the default profile (a `settings.json` backup is created).

## Editing experience

- [ ] Typing shows syntax highlighting; an unknown command is red.
- [ ] Autosuggestions from history appear dimmed; → accepts, Ctrl+→ accepts a word.
- [ ] Tab opens the completion menu (commands, parameters, paths) with descriptions; typing filters it.
- [ ] Ctrl+R fuzzy history search; Ctrl+R again switches scope (all / directory / session).
- [ ] Multi-line input (`if ($true) {` Enter) shows the continuation prompt.
- [ ] Shift+arrows select, Ctrl+C copies selection, Ctrl+V pastes (Windows clipboard), Ctrl+Z / Ctrl+Y undo/redo.
- [ ] Ctrl+C during `Start-Sleep 30` interrupts it; Ctrl+C on an empty line doesn't exit.
- [ ] `vim`/`notepad`/`ssh` (interactive native programs) work and the prompt returns cleanly.

## Panels (each opens, works, and Esc returns to a clean prompt)

- [ ] F1 command palette: fuzzy search panels/wizards/commands; Enter runs.
- [ ] Ctrl+T file picker inserts paths; Alt+C changes directory.
- [ ] Alt+G git panel: stage/unstage file and hunk, commit, branches, log, stash.
- [ ] Alt+J jobs: `Start-ThreadJob { Start-Sleep 5 }` appears, output preview, stop/remove.
- [ ] Alt+, settings: toggling autosuggestions takes effect immediately and persists.
- [ ] Alt+P processes: CPU/memory sparklines move; typing filters; F6/header click sorts; Enter shows path and
      command line; F4 lowers a Notepad's priority; Del ends it after confirming; F8 ends a `cmd /c start cmd` tree.
- [ ] Alt+N network: interface throughput updates every second; Connections lists processes; Tools: ping
      (live replies, Stop shows loss/min/avg/max), DNS lookup, Flush DNS cache shows ipconfig's output.
- [ ] Alt+D disks: volumes with usage bars; Enter on C: scans (F8 stops) and skips junctions like
      `Documents and Settings`; Enter/Backspace drill down/up; F2 cds there; F7 Disk Cleanup, F9 Disk Management (UAC).

## winget

- [ ] Alt+W winget panel: Installed / Upgrades / Search tabs populate (module or CLI backend shown).
- [ ] Search → Install a small package (e.g. `jqlang.jq`) with progress; it shows in Installed.
- [ ] Upgrades: select one → Upgrade; "Upgrade all" asks for confirmation.
- [ ] Sources → **Repair source (admin)** shows one UAC prompt, then succeeds
      (`Add-AppxPackage -Path 'https://cdn.winget.microsoft.com/cache/source.msix'`).
- [ ] Declining UAC shows "cancelled" and nothing breaks.
- [ ] `pk winget upgrades`, `pk winget search git`, `pk upgrade` (asks once, then upgrades everything).

## Windows Update

- [ ] Alt+U updates panel: "Check for updates" lists pending updates (or none) with KB numbers/sizes.
- [ ] Install selected → one UAC prompt → progress → result; reboot-required state shown.
- [ ] On a domain/Intune-managed PC the panel says "managed by your organization".
- [ ] `pk update check`, `pk update history`.

## Task Scheduler

- [ ] Alt+S scheduler panel browses folders; run/enable/disable a harmless task.
- [ ] `pk schedule add "every 30m" Get-Date --name test` creates `\Pickle\test`; it runs; `pk schedule remove test`.
- [ ] New task dialog with "run elevated" asks for UAC once and creates the task.

## Linux-style commands

- [ ] `ls -la`, `grep -rn foo .`, `cat -n file`, `rm -rf dir`, `touch x`, `which git`, `ps aux`, `df -h`, `du -sh .`,
      `head -n 5 f`, `tail -f log`, `export FOO=bar`, `FOO=1 some-cmd`, `echo hi 2>/dev/null`, `sudo …`,
      `apt install jq` (→ winget) behave sensibly; the translated command is shown dimmed.
- [ ] Unknown `ffmpeg` suggests `pk winget install Gyan.FFmpeg`.

## Wizards, aliases, plugins, sync

- [ ] Type `curl ` then F2: the curl wizard opens; presets fill fields; preview updates; Run executes.
- [ ] `pk wizard ffmpeg`, `pk wizard nmap`, `pk wizard robocopy` open and build valid commands.
- [ ] `pk alias add gco 'git checkout {branch}' --kind param` then `gco main` works; persists after restart.
- [ ] `pk plugin new hello` → install it → `pk hello` works; `pk plugin disable` removes it next start.
- [ ] `pk sync init <OneDrive folder>`; `pk sync push` on one PC, `pk sync pull` on another brings aliases/theme.

## Compatibility

- [ ] `Install-Module`/`Import-Module` from PSGallery works (e.g. `Install-PSResource Terminal-Icons`).
- [ ] With `shell.loadPwshProfile = true`, an existing pwsh profile using `Set-PSReadLineOption` loads without errors.
- [ ] `Enter-PSSession` to a remote machine works (if available).
