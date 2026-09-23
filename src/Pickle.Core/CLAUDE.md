# Pickle.Core

The shell engine. One folder per component; each component class is constructed by `PickleRuntime` with its final
name and pulls collaborators from the runtime lazily.

- Terminal output: `runtime.Terminal.Write(...)` only (ANSI allowed). Build strings with `Abstractions.Ansi` and
  measure with `TextWidth.VisibleWidth`.
- Running PowerShell: interactive user lines → `Engine.ExecuteInteractive`; everything else →
  `Engine.InvokeAsync`/`InvokeSilently` with parameters (never string-concatenate user input into scripts).
- Embedded modules: files under `Modules/<Name>/` are embedded automatically and extracted at startup; import them
  from your component's `OnStarted()` or via the Pickle module.
- Cmdlets: `[Cmdlet]` classes in `Cmdlets/` deriving `PickleCmdlet` are registered automatically.
- Tests: `tests/Pickle.Core.Tests/<Folder>/` mirroring this layout; use `TestPickle` + `VirtualTerminal` + `Snapshot`.
