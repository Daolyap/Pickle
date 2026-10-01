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
| Elevation (Windows) | `IElevationBroker` with an allowlisted operation; on Linux the privileged step goes through `IPrivilegedRunner` |

Rules a module must keep: warnings are errors; Windows-only code is `[SupportedOSPlatform("windows")]` and guarded;
a module's `Initialize` must be cheap (no process starts; do work lazily when a command or panel runs); a missing tool is
a friendly message, not an exception.
