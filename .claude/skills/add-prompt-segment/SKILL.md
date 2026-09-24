---
name: add-prompt-segment
description: Add a new prompt segment (e.g. rust version, aws profile, battery) to Pickle's themeable prompt — built-in in Pickle.Core or from a plugin — with tests and theme wiring.
---

# Add a prompt segment

A segment turns a `PromptContext` into a short piece of text. Themes place segments by `type` in
`prompt.left` / `prompt.right`; the `PromptEngine` applies the style's `template` (`{value}`, `{icon}`), colors
and separators.

## 1. Implement `IPromptSegment`

Built-in: `src/Pickle.Core/Prompt/Segments/<Name>Segment.cs`. Plugin: anywhere in the plugin, same interface.

```csharp
public sealed class RustSegment : IPromptSegment
{
    public string Type => "rust";   // the theme's "type"

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken ct) =>
        SegmentEnvironment.FindUpwards(context.Cwd, "Cargo.toml", directory: false) is not null
            ? SegmentText.Show("rust")
            : SegmentText.Hide();
}
```

- Return `null` (`SegmentText.Hide()`) to hide the segment. Return only the value; decoration belongs in the
  theme's `template`/`icon`. Colors in `PromptSegmentOutput` override the theme's only when state demands it.
- Read options with `style.Option("name")`, `style.OptionInt(...)`, `style.OptionBool(...)`.
- Pass anything taken from the filesystem, env vars or processes through `SegmentText.Sanitize` (no escape
  injection). Keep formatting in a `public static` pure function so it is unit-testable.
- **Cheap work** (context fields, env vars, a few `File.Exists`): complete synchronously.
- **Slow work** (spawning processes, git, network): make `RenderAsync` truly async. The engine caches async results
  per segment + directory + options, refreshes them after each command, and never waits longer than
  `prompt.gitTimeoutMs` (stale or hidden otherwise). Cache per-session facts yourself (see `NodeVersionProbe`), and
  spawn processes with `ProcessStartInfo.ArgumentList` and a timeout.
- Outside inputs (home dir, env, services) come from `SegmentEnvironment`; add a function there rather than calling
  `Environment`/`File` directly, and give `ThemePreview` a fixed sample value.

## 2. Register it

- Built-in: add it to `BuiltInSegments.Create` (`src/Pickle.Core/Prompt/Segments/BuiltInSegments.cs`).
- Plugin: `context.PromptSegments.Register(new RustSegment(...))` in `IPicklePlugin.Initialize`
  (registering an existing `Type` replaces it).

## 3. Use it in themes

Add to `themes/*.json` where it fits (e.g. `powerline.json` right side with a Nerd Font icon):

```json
{ "type": "rust", "foreground": "#282C34", "background": "#E5C07B", "template": " {icon} {value} ", "icon": "" }
```

Also document the type in `SegmentStyle.Type`'s summary (`src/Pickle.Abstractions/Theme.cs`) if it is built-in.

## 4. Test

- Unit tests in `tests/Pickle.Core.Tests/Prompt/SegmentTests.cs` for the pure formatter and hide/show rules.
- If a built-in theme uses it, re-record snapshots: `PICKLE_UPDATE_SNAPSHOTS=1 scripts/check.sh --quick --filter PromptRender`,
  review the diff of `__snapshots__/`, commit them.
- `scripts/check.sh --quick --filter Prompt`, then the full `scripts/check.sh`.
