# Pickle.Wizards

Declarative command wizards. A wizard = one JSON file in `Definitions/` (embedded) matching
`Abstractions/Wizards.cs` + tests in `tests/Pickle.Wizards.Tests` that build commands from presets and parse them back.
The engine is UI-free; the form UI lives in `Pickle.Tui/Panels/Wizard`. Recipe: `.claude/skills/add-wizard`.
