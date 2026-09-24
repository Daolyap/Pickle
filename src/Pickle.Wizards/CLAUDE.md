# Pickle.Wizards

Declarative command wizards. A wizard = one JSON file in `Definitions/` (embedded) matching
`Abstractions/Wizards.cs` + tests in `tests/Pickle.Wizards.Tests` that build commands from presets and parse them back.
The engine is UI-free; the form UI lives in `Pickle.Tui/Panels/Wizard`. Recipe: `.claude/skills/add-wizard`.

- `WizardEngine.Build` (values → PowerShell command line, errors, warnings) / `Parse` (PowerShell `Parser` AST →
  mode, values, unknown tokens kept verbatim). `PowerShellQuoting` decides bare vs single-quoted; never emit a field
  value without it (raw values are validated as a single expression / argument list).
- `WizardsPlugin` loads built-ins then `Paths.WizardsDir/*.json` (same id overrides) and registers `pk wizard`.
