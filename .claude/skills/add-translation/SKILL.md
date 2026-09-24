---
name: add-translation
description: Add Linux/bash-syntax translation to Pickle — an input rewriter (source-level rewrite of a typed line, like `export A=1` or `2>/dev/null`) or a POSIX shim command (a PowerShell function like `grep`/`ls` in the Pickle.Translate module or registered by a plugin) — with tests.
---

# Adding a translation

Pickle translates bash habits in two layers. Pick the right one:

| Layer | When | Where |
|---|---|---|
| **Input rewriter** (`IInputRewriter`) | Syntax PowerShell can't parse or means differently: `VAR=1 cmd`, `export`, `2>/dev/null`, `!!`, `sudo`, `apt install`. Runs only on lines typed at the prompt; the result is echoed as `→ <translated>`. | `src/Pickle.Core/Translation/Rewriters/` (built-in) or a plugin via `context.Translations.RegisterRewriter` |
| **Shim** (PowerShell function) | A command name that should exist: `grep`, `ls -la`, `du -sh`. Works in scripts and pipelines too. | `src/Pickle.Core/Modules/Pickle.Translate/Pickle.Translate.psm1` (built-in, Windows by default) or a plugin via `context.Translations.RegisterShim` |

## Input rewriter recipe

1. Create `src/Pickle.Core/Translation/Rewriters/<Name>Rewriter.cs` implementing `IInputRewriter`:
   - `Name`: short id (users can disable it via `translation.disabled` in config.json).
   - `Order`: existing order is history 10 → devnull 20 → packages 30 → export 40 → env-prefix 50 → sudo 60.
     Run before any rewriter that wraps text in `{ }` or quotes (those hide words from later rewriters).
   - `Rewrite(input, context)`: return `null` when untouched. **Never touch text inside strings/blocks**: use
     `ShellLexer.Segments/Statements/Words` (PowerShell-aware: quotes, backticks, `$( )`, `{ }`, comments) and only
     inspect `ShellWord`s with `IsPlain`/`Literal`. Apply changes with `TextEdits` (span replacements on the original).
   - Generate PowerShell with `PowerShellText.SingleQuote/QuoteIfNeeded/EscapeDoubleQuoted`; translate assignment values
     with `EnvValueTranslator.Translate`. Don't embed user text in executable positions it didn't already occupy.
   - Windows-only rewriters take `bool isWindows` in the constructor (tests pass `true`); runspace lookups are injected
     as `Func<…>` (see `SudoRewriter`, `PackageManagerRewriter`) so tests stay pure.
   - Return a one-line `Explanation` teaching the PowerShell equivalent.
2. Register it in `TranslationPipeline.Initialize()` and add a description to `RewriterDescriptions` (for `pk translate list`).
3. Tests: add `[Theory]` rows to `tests/Pickle.Core.Tests/Translation/RewriterTests.cs` — the rewrite table **and** a
   no-op table (the word inside single quotes, double quotes, `{ }`, mid-sentence, other platform).
4. If it changes execution behavior, add an end-to-end case to `tests/Pickle.E2E/e2e_w4_translation.py`.

## Shim recipe (built-in module)

1. Add `function <name> { … }` to `Pickle.Translate.psm1` in the right section. Conventions:
   - Parse options with `Read-PosixArgs -Command <name> -Arguments $args -Flags '…' -ValueFlags '…' -Long @{…}`;
     `if ($o.Error) { return Write-Error $o.Error }`. `$o.Opt` is case-sensitive (`-r` ≠ `-R`).
   - Resolve operands with `Resolve-PosixPath` (literal first, then wildcards) / `Get-FullPath` (may not exist).
     Paths are relative to the PowerShell location, never the process cwd.
   - Use full cmdlet names inside the module (`Sort-Object`, not `sort` — that is a native tool on Linux).
   - Return objects where natural (`ls`, `du`, `ps`), strings where POSIX tools print text (`grep`, `cat`).
   - Errors: `Write-Error "<message>"` (PowerShell prefixes the shim name). Destructive shims must refuse
     `Get-ProtectedReason` paths (roots, home and its ancestors).
   - Pipeline input: use `begin/process/end` with `$MyInvocation.ExpectingInput` (see `grep`, `head`).
2. Add the name to `$script:AllShims` in the psm1, `FunctionsToExport` in the psd1 and `ShimCatalog.Shims` (C#).
   `ShimModuleTests.ModuleExportsExactlyTheCatalog` fails if they drift. The module removes a conflicting built-in
   alias (`ls` → `Get-ChildItem`) while loaded and Pickle restores it on unload.
3. Tests in `tests/Pickle.Core.Tests/Translation/ShimModuleTests.cs` — the fixture force-loads the module on Linux
   (`TranslationPipeline.LoadShims(force: true)`) inside a temp directory.

## Shim from a plugin

```csharp
context.Translations.RegisterShim(new TranslationShim("pbcopy", "Copy stdin to the clipboard", "$input | Set-Clipboard"));
```

The body becomes `function global:pbcopy { … }` at startup (names are validated; `translation.disabled` applies).
`$input`/`$args` work as in any PowerShell function.

## Checklist

- `scripts/check.sh --quick --filter RewriterTests` / `--filter ShimModuleTests`, then the full `scripts/check.sh`.
- `pk translate list` shows the new entry; `pk translate off` disables everything.
