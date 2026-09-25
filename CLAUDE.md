# Pickle — agent guide

Pickle is a Windows-first shell that hosts the **real PowerShell 7 engine** (Microsoft.PowerShell.SDK, in-process,
custom `PSHost`) and replaces the interactive experience: its own line editor (syntax highlighting, autosuggestions,
completion menu, fuzzy history), a themeable prompt, Terminal.Gui panels (files, git, jobs, processes, network, network tools, disks, winget, Windows Update,
Task Scheduler, Windows Sandbox, settings, command wizards), built-in network tools, on-demand tool installs, a plugin system,
Linux-syntax translation, aliases and sync.
C# / .NET 10. GitHub repo: `Daolyap/Pickle` (formerly `milkshell`); the product, binary (`pickle`) and namespaces are **Pickle**.

## Commands

| What | Command |
|---|---|
| Everything (restore, build with warnings-as-errors, format check, tests) — run before every commit | `scripts/check.sh` (`scripts/check.ps1` on Windows) |
| Fix formatting | `scripts/check.sh --fix` |
| Only some tests | `scripts/check.sh --quick --filter LineEditor` |
| Real-terminal end-to-end tests (pty + pyte) | `scripts/check.sh --e2e` or `python3 tests/Pickle.E2E/run_e2e.py -k vim` |
| Run the shell | `dotnet run --project src/Pickle` (or `src/Pickle/bin/Debug/net10.0/pickle`) |
| Run one command | `pickle -c 'Get-Date'` · headless (stdin lines): `pickle --headless` |
| Single-file binaries | `scripts/publish.sh win-x64 linux-x64` → `artifacts/publish/<rid>/` (Fedora RPM: `packaging/rpm/build-rpm.sh`) |
| Code knowledge graph ([graphify](https://github.com/Graphify-Labs/graphify)) | `scripts/graph.sh`, then `graphify query "…"` / `graphify explain X` / `graphify path A B` |

.NET lives in `~/.dotnet` in cloud sessions (the SessionStart hook installs it and sets `PATH`/`DOTNET_ROOT`).
Tests use xunit.v3 on Microsoft.Testing.Platform: `dotnet test --solution Pickle.slnx`, filters go after `--`
(e.g. `-- --filter-method "*Snapshot*"`). Isolate state with `PICKLE_HOME=<dir>` when running the binary by hand.

## Layout

Diagrams (projects, data flow, trust boundaries, feature → folder): `docs/architecture.md`. Hubs, communities and
cross-file links from graphify: `docs/graph-report.md` (the full `graphify-out/graph.json` is git-ignored; rebuild it
with `scripts/graph.sh` in ~10 s and query it instead of grepping when you need "what calls/uses X").

```
src/Pickle.Abstractions  Contracts only (plugins, registries, services, theme/config models, KeyChord, Ansi, TextWidth).
                         Plugins reference just this. Changing it affects everyone — add, don't break.
src/Pickle.Core          The shell engine. Key folders:
  Hosting/               PickleHost/UI/RawUI (PSHost), ShellEngine (runspace, IPickleShell), Repl, ProgressPane
  Terminal/              ITerminal + ConsoleTerminal. ALL terminal I/O goes through ITerminal.
  Input/                 LineEditor (ILineEditor + IEditorBuffer), DefaultKeyBindings
  Syntax/ Render/        Highlighter (PowerShell tokenizer → styles), frame renderer
  History/ Completion/   JSONL history, autosuggest, fuzzy matcher, completion engine + menu overlay
  Prompt/                ThemeProvider, PromptEngine, segments
  Aliases/ Translation/ Profile/   alias functions, Linux-syntax rewriters + shims, profile loading
  Config/ Plugins/ Sync/ Git/      config store, plugin host, sync, git CLI service
  System/                Process, network and disk monitors (/proc on Linux, Win32 on Windows) for the system panels
  Cmdlets/               [Cmdlet] classes (auto-registered). `pk` = Invoke-PickleCommand
  Modules/               Embedded .psm1/.psd1 modules (extracted at startup to DataDir/modules/<hash>)
  Contracts/             Internal seams between Core components (IPromptRenderer, IAutosuggestProvider, ...)
  PickleRuntime.cs       Composition root inside the process; implements IPickleContext
  PickleApp.cs           Mode dispatch (interactive / -c / file / headless)
src/Pickle.Tui           Terminal.Gui v2 panels. PanelHost (IPanelHost), TuiPlugin, Panels/<Feature>/
src/Pickle.Windows       Windows services (winget, WUA, Task Scheduler, elevation broker, Windows Terminal fragment)
src/Pickle.Wizards       Wizard schema engine + Definitions/*.json (embedded)
src/Pickle.Network       Network tools engines (scan, sweep, DNS client, trace, whois, cert, subnet, http, WoL) + pk commands
src/Pickle               pickle.exe: Program.cs (arg modes) + BuiltInPlugins.cs (the list of built-in plugins)
tests/Pickle.Testing     VirtualTerminal, TestPickle, Snapshot, Fakes/ — shared test doubles
tests/*.Tests            xunit.v3 per project · tests/Pickle.E2E pty harness (Python)
themes/*.json            Built-in themes (embedded into Pickle.Core)
```

## How things fit

- Startup: `Program` → `PickleApp.Run` → `new PickleRuntime` (constructs every component with its final class) →
  `InitializeComponents()` (components register actions/commands/segments) → `Start()` (open runspace, load plugins,
  `OnStarted()`, profile) → `Repl.Run()`.
- Components get collaborators from `PickleRuntime` **lazily** (never in constructors). Implement
  `Contracts.IRuntimeComponent` for `Initialize`/`OnStarted`.
- Built-in features are `IPicklePlugin`s listed in `src/Pickle/BuiltInPlugins.cs`; they only see `IPickleContext`.
- The REPL runs typed lines with `AddScript(line).AddCommand("Out-Default")` so native programs own the console.
  `$?`/`$LASTEXITCODE` are read right after (`ShellEngine.QueryStatus`).
- Threading: interactive pipelines run synchronously from the REPL thread. `IPickleShell.InvokeAsync` runs nested
  when called from inside a pipeline, waits for idle otherwise; `ShellTarget.Background` uses a runspace pool.
- Panels run modally on the REPL thread via `IPanelHost.Show` (fresh Terminal.Gui `IApplication` each time) and
  return a `PanelResult` (insert text / replace input / run command / cd).
- Cmdlets find their runtime with `PickleRuntime.Resolve(Host)` (derive from `PickleCmdlet`).

## Rules

- **Warnings are errors.** Nullable is on. Run `scripts/check.sh` before committing; CI runs it on Linux + Windows.
- **Windows-only code** (winget, WUA COM, Task Scheduler, registry, P/Invoke): mark `[SupportedOSPlatform("windows")]`
  and guard callers with `OperatingSystem.IsWindows()` — the CA1416 analyzer enforces this. Every Windows service
  has an interface in Abstractions and a fake in `tests/Pickle.Testing/Fakes` so UI/commands test on Linux.
- **Never touch `System.Console` outside `ConsoleTerminal`** (and the elevated helper). Use `ITerminal` so
  `VirtualTerminal` tests see everything.
- **No shell-string injection**: pass user values to PowerShell as parameters
  (`InvokeAsync("param($p) ...", new Dictionary<string, object?>{["p"]=value})`) and to processes via
  `ProcessStartInfo.ArgumentList`. When generated script text must embed a value, use `PowerShellText.SingleQuote`
  (PowerShell also treats ‘ ’ ‚ ‛ as quotes — never hand-roll `Replace("'", "''")`).
- **Never start a program by bare name**: resolve it with `Commands.ExecutableLocator.Find` (absolute PATH entries only;
  Windows and .NET's Unix resolver would otherwise run a copy planted in the current directory). Automatic git calls
  go through `GitService`, which also disables repo-configured fsmonitor/filters/textconv.
- Package versions live only in `Directory.Packages.props`. Don't add packages without need.
- New config setting: add a property with a default in `Abstractions/Config.cs` (and the JSON schema).
- New `pk` subcommand: derive from `PickleCommandBase` (Abstractions: `CommandArgs`, `CommandOutput`, `Display.Columns`)
  or implement `IPickleCommand`; register it in your plugin's/component's `Initialize`. Plugin authoring guide:
  `docs/plugins.md` (keep it in sync when plugin-facing APIs change).
- New cmdlet: `[Cmdlet]` class deriving `PickleCmdlet` in `Pickle.Core/Cmdlets` — registration is automatic.
- New key action: `KeyBindings.RegisterAction(name, ...)`; default chord goes in `Input/DefaultKeyBindings.cs`.
- Comments: only for non-obvious *why*. No multi-paragraph docstrings.
- Recipes for common additions live in `.claude/skills/` (add-wizard, add-panel, add-prompt-segment, add-translation, release).

## Testing

- `using var t = TestPickle.Create(start: true);` gives an isolated runtime with an open runspace and a
  `VirtualTerminal` (`t.Terminal.Type("ls").Press("Tab", "Enter")`, `t.Terminal.GetScreenText()`,
  `GetStyledScreen()` shows colors as `«fg=#B5E36B,bold»text«»`).
- Snapshots: `Snapshot.Match(t.Terminal.GetScreenText())` → `__snapshots__/<Class>.<Method>.txt` next to the test.
  First run writes it (commit it!); `PICKLE_UPDATE_SNAPSHOTS=1` re-records. CI fails on missing snapshots.
- Terminal.Gui: `TuiHarness.InitApp()` (virtual time + headless ANSI driver on every OS — never a bare `app.Init()`,
  which gets the console driver on Windows CI), input injection (`app.InjectKey(...)`), `StopAfterFirstIteration = true`.
- E2E: add `test_*` functions to `tests/Pickle.E2E/run_e2e.py` (real pty; covers native programs and panels).

## Gotchas

- Single-file publish: PowerShell's built-in modules live under `runtimes/<os>/lib/net10.0/Modules` and are added to
  `PSModulePath` explicitly (`ShellEngine.FindBundledModulesDirectories`). PSResourceGet, ThreadJob and Archive are
  not in the SDK: `src/Pickle/BundledModules.targets` downloads them (pinned SHA-256) into `<output>/Modules/`;
  cache in `artifacts/module-cache`, `-p:PickleBundleModules=false` for offline builds. NativeAOT is not possible (PowerShell uses
  reflection/dynamic code).
- Terminal.Gui v2 must use the **instance** model (`Application.Create()`); never touch the static
  `Application.Init/Run` (it throws once the instance model was used in-process).
- The Verify snapshot library is intentionally not used (its build-time license check); use `Pickle.Testing.Snapshot`.
- `$PSStyle` is one static instance per process, shared by every runspace. Tests asserting on it (or other
  process-wide PowerShell state) must use `[Collection(ProcessWideStateCollection.Name)]` (non-parallel).
- `PSReadLine` is not used; a shim module maps common `Set-PSReadLineOption` calls to Pickle config.
