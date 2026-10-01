# Optional modules

A **module** is an optional feature that ships inside `pickle.exe` but stays switched off until someone selects it, so a
default install carries no Docker, Kubernetes or GitHub code paths at startup. Modules are ordinary plugins
(`IPicklePlugin`) plus a `ModuleDescriptor`; third-party plugins (`docs/plugins.md`) are a different mechanism for code
that does not live in this repository.

| Module id | What it adds | Needs |
|---|---|---|
| `nmap` | `pk nmap`, scan builder panel (Alt+Shift+N), XML results as objects | `nmap` |
| `docker` | containers/images/volumes/compose panel (Alt+Shift+D), `pk docker` | `docker` or `podman` |
| `kubernetes` | contexts/namespaces/pods/deployments panel (Alt+Shift+K), `pk k8s` | `kubectl` |
| `github` | pull requests, issues and workflow runs panel (Alt+Shift+G), `pk gh` | `gh` |
| `languages` | python, java, ruby, php, terraform, deno, bun, … version prompt segments | the tools themselves |
| `notifier` | desktop notification when a long command finishes (`pk notify`) | none (toast / `notify-send` / `osascript`) |
| `explain` | `pk explain <command line>` and an editor key: what each part does, offline | none |
| `vault` | `pk secret` + `pk secret run`: secrets in the OS store, injected into one command | Credential Manager / libsecret / Keychain |
| `wsl` | WSL distros on Windows, Distrobox/Toolbx/LXC on Linux (Alt+Shift+W) | `wsl.exe` / `distrobox` |
| `themes` | extra theme packs | none |

`pk module list` shows the real list for your machine.

## Turning modules on

Pickle decides per user: `installer selection + modules.enabled − modules.disabled`.

| How | Where |
|---|---|
| MSI | Customize page: one feature per module; writes `HKLM\Software\Pickle\Modules\<id> = 1` |
| RPM | `dnf install pickle-module-docker` drops `/etc/pickle/modules.d/docker` (`pickle-modules-all` for everything) |
| Portable, Scoop, winget, tar.gz | first start (and `pk setup`) shows a checklist; or `PICKLE_MODULES=docker,nmap`; or a `modules.d/<id>` marker file next to the exe |
| Any | `pk module enable docker` (loads immediately) · `pk module disable docker` (gone at next start) · `pk module setup` (checklist) |

`pk module list --all` also shows hidden modules such as `example`.

### In the installers

`packaging/modules.json` lists the modules the installers offer (every module except the hidden `example`);
`packaging/modules.py` turns it into the installer pieces, and `PackagingTests` fails when the file and the code disagree.

| Installer | What a module becomes |
|---|---|
| MSI | An unticked feature `Module_<id>` under *Optional modules* in the Customize page, writing `HKLM\Software\Pickle\Modules\<id> = 1`. Choices survive a major upgrade (each feature starts ticked when the old version wrote its value). Silent installs: `msiexec /i pickle.msi /qn ADDLOCAL=Main,Module_docker,Module_vault` (`ADDLOCAL=ALL` for everything). |
| RPM | A noarch sub-package `pickle-module-<id>` that only drops `/etc/pickle/modules.d/<id>` and recommends the tool the module drives (`podman`, `nmap`, `gh`, `kubernetes-client`, `libsecret`, `distrobox`…) as a weak dependency; `pickle-modules-all` depends on every one. `dnf remove pickle-module-docker` turns it off again. |
| Scoop, winget, portable exe, tar.gz | Nothing is installed with them: the first start offers the checklist, or set `PICKLE_MODULES=docker,nmap`, or put an (empty) file named after the module in a `modules.d` folder next to the executable. |

Adding a module means adding its entry to `packaging/modules.json` (id, name, description exactly as in its
`ModuleDescriptor`, platforms, `rpmRecommends`); the MSI feature, the RPM package and the marker all follow from that.

## Notes on the modules

- **vault**: `pk secret set|get|list|remove|run|lock|status`. Backends: Windows Credential Manager (2560 bytes per secret),
  the Secret Service through `secret-tool` (libsecret), the macOS Keychain through `security -i`, and an encrypted file
  (AES-256-GCM, PBKDF2-SHA256 600 000 rounds, key kept in memory for `extensions.vault.unlockMinutes`) when none works.
  `extensions.vault.backend` picks one (`auto` by default). `pk secret get` returns a `SecureString` (`--plain` for text);
  `pk secret run -e GH_TOKEN=github-token gh api user` sets the variable only while that command runs. Names are lower-case
  `[a-z0-9._-]`, at most 64 characters.
- **wsl**: backends are found by their tools: `wsl.exe`, `distrobox`, `toolbox` (with `podman`), `lxc` and `incus`. Names and
  images are validated before they reach any program; removing or wiping always asks, and a backend that fails does not hide the
  others. `pk wsl` and `pk distros` are the same command.
- **explain**: offline only; it never runs an unknown program to ask for `--help`. Your own knowledge files go in
  `<data dir>/explain/*.json` (same format as `src/Pickle.Modules/Explain/pack.json`) and override the built-in entries.
- **themes**: copies its themes into your themes folder once (yours are never overwritten).

## Writing a module

Copy `src/Pickle.Modules/Example/` (it is built and tested on every run, so it cannot rot) and follow the
`add-module` skill (`.claude/skills/add-module/SKILL.md`). In short:

1. `src/Pickle.Modules/<Name>/<Name>Module.cs`: a class implementing `IPicklePlugin` with a static
   `ModuleDescriptor` (`Id`, `Name`, `Description`, `Create`, `Platforms`, `Tools`, `Provides`).
2. Register the descriptor in `src/Pickle.Modules/OptionalModules.cs`.
3. Add its id, name and description to `packaging/modules.json` (a test fails when the two drift apart).
4. Tests in `tests/Pickle.Modules.Tests/<Name>/` using `FakeProgramRunner` for every external program.

What a module can use (all through `IPickleContext`, never `System.Console`, never a bare process name):

| Need | Use |
|---|---|
| Run `docker`, `kubectl`, `gh`… | `context.Services.Require<IProgramRunner>()`: argument list only, PATH-absolute, timeout, stdin for secrets |
| A list-and-actions panel | derive `ResourcePanel<T>` (`Pickle.Tui`) and register a `PanelDescriptor` |
| `pk <name>` that opens the panel interactively and lists otherwise | derive `PanelCommand` |
| Settings | a class stored under `extensions.<id>` via `context.Config.Get<T>(id)` / `Set` (`pk config set extensions.docker.runtime podman`) |
| Prompt segment | `IPromptSegment` → `context.PromptSegments.Register` (add `{ "type": "…" }` to a theme) |
| Hook | `context.Hooks.Register(HookKind.PostExecute, …)` |
| Command lines for the shell | `PowerShellQuote.Command("docker", "logs", "-f", id)`; never concatenate user input |
| Elevation (Windows) | `IElevationBroker` with an allowlisted operation; on Linux the privileged step goes through `IPrivilegeService` |

Rules a module must keep: warnings are errors; Windows-only code is `[SupportedOSPlatform("windows")]` and guarded;
a module's `Initialize` must be cheap (no process starts; do work lazily when a command or panel runs); a missing tool is
a friendly message, not an exception.
