---
name: add-wizard
description: Add or change a command wizard (the F2 form that builds a curl/git/docker/... command line) — write the JSON definition in src/Pickle.Wizards/Definitions, add presets, and pass the validation, snapshot and round-trip tests.
---

# Add a command wizard

A wizard is one JSON file: `src/Pickle.Wizards/Definitions/<id>.json` (embedded automatically; users can drop the
same format into `<config>/wizards/*.json`, which overrides a built-in with the same id). Schema:
`src/Pickle.Abstractions/Wizards.cs`. The engine (`WizardEngine.Build/Parse`) and the Terminal.Gui form
(`src/Pickle.Tui/Panels/Wizard`) need no code changes.

## 1. Write the definition

```json
{
  "id": "mytool", "title": "MyTool", "description": "What it does, one line.",
  "command": "mytool", "aliases": ["mytool.exe"],
  "wingetId": "Publisher.MyTool",          // only if you are sure it exists (offers "Install" when missing)
  "homepage": "https://…", "windowsOnly": false,
  "sections": [ { "title": "Main", "options": [ … ] } ],
  "presets": [ { "name": "…", "description": "…", "values": { "optionId": "value" } } ]
}
```

Option `type`s (camelCase): `flag`, `text`, `number`, `path` (file picker), `choice`, `list` (one item per line,
flag repeated), `keyValueList` (`keyValueSeparator` ": " or "="), `positional`.

- `flag` / `flagAliases`: first is emitted, aliases are accepted when parsing. Must be plain words PowerShell passes
  through unquoted (`-X`, `--request`, `/MIR`, `-c:v`, netsh-style `listenport`).
- `valueStyle`: `space` (`-o file`), `equals` (`--out=file`; with a dash-less flag gives `name=value`), `none`
  (`-ofile`, `/R:3` — put the `:` in the flag).
- `position` makes an option positional (any type; `list` = variadic, `path` gets the picker). Positions < 1 are
  emitted *before* the options (robocopy `source dest /MIR`); positionals with a `flag` emit it once as a marker
  (`"flag": "--"` for `git log -- paths`, `kubectl exec pod -- cmd`).
- `choice` without `flag` and without `position`: the choice values *are* the flags (`--soft`/`--hard`, `-c`/`-x`).
- `default`: the tool's own default — not emitted when equal. `required`, `validation` (regex), `dependsOn`
  (`"id"`, `"id=a|b"`, `"!id"`) hides/omits the option.
- `template`: composite value with sub-fields keyed `optionId.sub`, e.g. `"[{bindAddress}:]{localPort}:{remoteHost}:{remotePort}"`;
  `[...]` is optional, `\[ \] \{ \}` escape.
- Local extensions (not yet in Abstractions): `"warning": "…"` on an option or a choice (dangerous options such as
  `/MIR`, `--delete`, `--hard`), and `"raw": true` for PowerShell source values: a raw template's text is emitted
  verbatim with sub-fields turned into safe literals (`@{LogName={logName}}`), a raw non-template value must be one
  expression, and a raw *positional* takes the rest of the line (`docker run image <command…>`).
- With `modes` (subcommands: `"subcommand": ["compose", "up"]`), top-level `sections` are global options emitted
  before the subcommand (`git -C dir`, `adb -s serial`). Modes with an empty subcommand are told apart by fewest
  unknown tokens (Get-WinEvent).
- Mark Windows-only tools `"windowsOnly": true`. Scanners and similar tools: say in the description that they are
  only for systems you are authorized to test.

## 2. Presets

3–10 genuinely useful recipes per wizard. `mode` is required when the wizard has modes; flag values are `"true"`;
lists use `\n`; template fields use `"optionId.sub"`. Only use flags you have verified against the tool's docs.

## 3. Test

```bash
PICKLE_UPDATE_SNAPSHOTS=1 scripts/check.sh --quick --filter Wizard   # records the preset snapshot
scripts/check.sh --quick --filter Wizard
```

`tests/Pickle.Wizards.Tests/DefinitionTests.cs` covers every definition automatically: it validates the schema
(unique ids/flags/positions, choices, presets reference real options), builds every preset without errors, parses each
built command line back to the same values with no unknown tokens (round trip), and snapshots all preset command
lines in `__snapshots__/DefinitionTests.PresetCommandLines.txt` — read the diff line by line, it is the review of
your wizard, then commit the snapshot. Add the id to `AllExpectedWizardsAreEmbedded`. If a round trip fails, the
definition is usually ambiguous (two options with the same flag, a variadic positional before another positional)
— fix the JSON rather than the engine.

Try it: `pk wizard mytool --presets`, `pk wizard mytool --print --preset 1`, or type `mytool …` and press F2.
