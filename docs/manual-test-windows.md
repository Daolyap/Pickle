# Manual test checklist (Windows)

Automated tests cover the engine, editor, parsers and panels (with fakes) on Linux and Windows CI. These checks
need a real Windows desktop: UAC, winget, Windows Update, Task Scheduler and Windows Terminal. Tick them off
after installing a build.

## Install

- [ ] **MSI**: `pickle-<ver>-win-x64.msi` installs without errors; `pickle --version` works in a *new* terminal
      (PATH updated); Start menu has "Pickle" with the pickle icon; Apps & features shows the pickle icon.
- [ ] `pickle.exe` shows the pickle icon in Explorer and on the taskbar; `pk help` starts with the pickle logo.
- [ ] **Portable**: `pickle.exe` runs from any folder; the first interactive start asks "Add a Pickle profile to Windows
      Terminal? [Y/n]" (only if Terminal is installed and the MSI's profile isn't); Y adds it, and it isn't asked again.
- [ ] Startup banner: pickle art with a short shine; pressing a key during it skips the animation and the key is typed.
      `pk config set shell.bannerStyle line` (or `art`) changes it.
- [ ] **winget / Scoop** (after publishing): `winget install Daolyap.Pickle`, `scoop install pickle/pickle`.
- [ ] Uninstall (Apps & features) removes the exe, PATH entry, Start menu item and the Terminal fragment.

## Windows Terminal

- [ ] Windows Terminal shows a **Pickle** profile (Settings → Profiles) with the Pickle color scheme and the pickle
      icon in the tab and the new-tab menu (both the MSI's profile and one added by the portable exe).
- [ ] `pk terminal set font "Cascadia Code NF"` / `pk terminal set opacity 80` update the profile live.
- [ ] `pk theme set powerline` changes the prompt *and* the Terminal color scheme.
- [ ] `pk terminal default` makes Pickle the default profile (a `settings.json` backup is created).

## Editing experience

- [ ] Typing shows syntax highlighting; an unknown command is red, but `apt install jq` / `sudo …` / `export X=1` are not.
- [ ] Tab on a quoted path (`cd 'C:\Program Fi` Tab) completes to `'C:\Program Files\'` with the cursor *inside* the
      quotes; Tab again completes the next folder.
- [ ] Output is aligned, never a "staircase": `pk help`, `pk winget upgrades`, `pk schedule list`, `git log --oneline -5`.
- [ ] Autosuggestions from history appear dimmed; → accepts, Ctrl+→ accepts a word.
- [ ] Tab opens the completion menu (commands, parameters, paths) with descriptions; typing filters it.
- [ ] Ctrl+R fuzzy history search; Ctrl+R again switches scope (all / directory / session).
- [ ] Multi-line input (`if ($true) {` Enter) shows the continuation prompt.
- [ ] Shift+arrows select, Ctrl+C copies selection, Ctrl+V pastes (Windows clipboard), Ctrl+Z / Ctrl+Y undo/redo.
- [ ] Ctrl+C during `Start-Sleep 30` interrupts it; Ctrl+C on an empty line doesn't exit.
- [ ] `vim`/`notepad` (interactive native programs) work and the prompt returns cleanly.
- [ ] `ssh user@host` shows the host-key question / password prompt, gives a working remote shell, and `exit` returns
      to a clean Pickle prompt.

## Panels (each opens, works, and Esc returns to a clean prompt)

- [ ] F1 command palette: fuzzy search panels/wizards/commands; Enter runs.
- [ ] Ctrl+T file picker inserts paths; Alt+C changes directory.
- [ ] Alt+G git panel: stage/unstage file and hunk, commit, branches, log, stash.
- [ ] Alt+J jobs: `Start-ThreadJob { Start-Sleep 5 }` appears, output preview, stop/remove.
- [ ] Alt+, settings: toggling autosuggestions takes effect immediately and persists.
- [ ] In settings, → enters the selected category's fields and ← returns to the category list (text fields: ← first
      moves the cursor, then leaves at the start).
- [ ] Alt+P processes: CPU/memory sparklines move; typing filters; F6/header click sorts; Enter shows path and
      command line; F4 lowers a Notepad's priority; Del ends it after confirming; F8 ends a `cmd /c start cmd` tree.
- [ ] Alt+N network: interface throughput updates every second; Connections lists processes; Tools: ping
      (live replies, Stop shows loss/min/avg/max), DNS lookup, Flush DNS cache shows ipconfig's output.
- [ ] Alt+D disks: volumes with usage bars; Enter on C: scans (F8 stops) and skips junctions like
      `Documents and Settings`; Enter/Backspace drill down/up; F2 cds there; F7 Disk Cleanup, F9 Disk Management (UAC).

## winget

- [ ] Alt+W winget panel: Installed / Upgrades / Search tabs populate (module or CLI backend shown).
- [ ] Search with the keyboard only: type a query, Enter → focus jumps to the results; ↑↓ shows details;
      Enter (or `i`) → confirmation → installs (e.g. `jqlang.jq`) with progress; it shows in Installed.
- [ ] Upgrades: every row starts ticked; click a row to untick it, Shift+click (or Alt+click if the terminal
      keeps Shift+click for text selection) another row → the whole range takes the first row's state; Space,
      Shift+↑/↓ and Ctrl+A (or the header checkbox) work too. F5 Refresh keeps the unticked rows unticked.
- [ ] F9 / **Upgrade selected** on the Upgrades tab upgrades the ticked rows; on the Installed tab with packages
      ticked it upgrades those (only ones with an upgrade; the dialog says how many were skipped).
- [ ] Installed: tick two packages → F8 / **Uninstall** lists both → **Uninstall** removes them (progress and
      failures with output in the pane); **As administrator** does the same after one UAC prompt.
- [ ] Sources → **Repair source** (no UAC) and **Repair as admin** (one UAC prompt) show the full
      `Add-AppxPackage` output in the "Repair output" pane; a failure shows the HRESULT and the AppX log lines.
- [ ] Declining UAC shows "declined" and nothing breaks.
- [ ] `pk winget list` / `upgrades` / `search git` print a table (Id, Version, Available, Name) that fits
      80- and 120-column windows; `(pk winget list).Id` still returns ids.
- [ ] `pk winget uninstall <id> <id> [--elevated]`, `pk winget repair-source [--admin] --verbose` (output shown),
      `pk upgrade` (asks once, then upgrades everything).

## Windows Update

- [ ] Alt+U updates panel: **Check** shows progress in the Progress pane and lists pending updates (or none)
      with KB numbers/sizes; security/other start ticked, drivers/optional unticked.
- [ ] Tick/untick with the same mouse and keys as the winget lists (Shift+click range, Space, Ctrl+A); a
      second Check keeps your ticks.
- [ ] Install selected → one UAC prompt → progress → result; reboot-required state shown.
- [ ] F7 / **Install KB…**: type a KB number (e.g. an optional preview update) → it is found even if not listed
      → confirm → installs; an unknown KB says it is not offered for this PC.
- [ ] On a domain/Intune-managed PC the panel says "managed by your organization".
- [ ] `pk update check` shows a spinner line with elapsed time and finishes (or Ctrl+C stops it at once;
      `--timeout 30s` gives up with a clear message); `pk update history`.
- [ ] `pk update install --kb KB5031455` (also optional/driver updates; an already-installed KB says so).

## Task Scheduler

- [ ] Alt+S scheduler panel browses folders; run/enable/disable a harmless task.
- [ ] `pk schedule add "every 30m" Get-Date --name test` creates `\Pickle\test`; it runs; `pk schedule remove test`.
- [ ] New task dialog with "run elevated" asks for UAC once and creates the task.

## Linux-style commands

- [ ] `ls -la`, `grep -rn foo .`, `cat -n file`, `rm -rf dir`, `touch x`, `which git`, `ps aux`, `df -h`, `du -sh .`,
      `head -n 5 f`, `tail -f log`, `export FOO=bar`, `FOO=1 some-cmd`, `echo hi 2>/dev/null`, `sudo …`,
      `apt install jq` (→ winget) behave sensibly; the translated command is shown dimmed.
- [ ] Unknown `ffmpeg` suggests `pk winget install Gyan.FFmpeg`.

## Missing tools, network tools, sandbox

- [ ] With 7-Zip not installed, `7z a test.7z somefile` asks first ("'7z' isn't installed…"). `P` toggles "add to
      PATH"; Enter installs just for you, then the command runs and `7z` still works in a new Pickle window.
- [ ] Same with `T` (this session only): the tool works now; after `exit` Pickle removes it ("Removing temporary
      tools…") and it's gone from Apps & features. `N` runs the command anyway and doesn't ask again; Esc cancels.
- [ ] `pk tool install jq --temp`, `pk tool install nmap --machine` (UAC), `pk tool list zip`.
- [ ] The wizard's "Install 7z" button asks for scope and PATH in a dialog.
- [ ] `pk scan 127.0.0.1 -p top100 --banner` lists open ports as a table; `pk scan <your subnet>/24 -p 445,3389` finishes
      in seconds; `pk sweep <your subnet>/24` shows hosts with names and MAC addresses (router, printers…).
- [ ] `pk dns example.com all`, `pk dns 8.8.8.8`, `pk dns example.com MX --server 1.1.1.1`, `pk trace 1.1.1.1`,
      `pk whois example.com`, `pk cert github.com --chain`, `pk http https://github.com --headers`,
      `pk subnet 10.1.2.3/22 --split /24`, `pk ip --public`.
- [ ] Alt+T opens Network tools: each tool runs from its form (Enter or F5), results stream in, F6 stops a long scan,
      F8 puts the `pk` command on the prompt.
- [ ] Alt+X opens Windows Sandbox. If the feature is off, F9 turns it on (UAC, then restart). Launch "Safe browsing"
      (Edge opens), "Test an installer" (Downloads is on the sandbox desktop, read-only, no network) and "Pickle
      inside" (Pickle starts in the sandbox). Edit a preset, F2 saves it under a new name, F3 exports a `.wsb` you can
      double-click, F4 previews the `.wsb` and setup script.
- [ ] `pk sandbox run "Try apps with winget" --winget Git.Git` installs winget and Git inside the sandbox
      (`pickle-setup.log` on its desktop shows each step).

## Wizards, aliases, plugins, sync

- [ ] Type `curl ` then F2: the curl wizard opens; every text field is a full rounded box; presets fill fields;
      preview updates; Run executes.
- [ ] `pk wizard ffmpeg`, `pk wizard nmap`, `pk wizard robocopy` open and build valid commands (nmap: host discovery,
      timing, evasion and output sections; 18 presets).
- [ ] `pk alias add gco 'git checkout {branch}' --kind param` then `gco main` works; persists after restart.
- [ ] `pk plugin new hello` → install it → `pk hello` works; `pk plugin disable` removes it next start.
- [ ] The examples in `docs/plugins.md` (a `Register-PicklePanel` with `-Preview`, `-RefreshSeconds` and `-Command`; a
      `Register-PickleWizard` for your own function) work when pasted into a plugin.
- [ ] `pk sync init <OneDrive folder>`; `pk sync push` on one PC, `pk sync pull` on another brings aliases/theme.

## Compatibility

- [ ] `Install-Module`/`Import-Module` from PSGallery works (e.g. `Install-PSResource Terminal-Icons`).
- [ ] With `shell.loadPwshProfile = true`, an existing pwsh profile using `Set-PSReadLineOption` loads without errors.
- [ ] `Enter-PSSession` to a remote machine works (if available).
