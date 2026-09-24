---
name: add-panel
description: Add a full-screen Terminal.Gui panel to Pickle (a PanelWindow subclass registered by a plugin, with key binding, palette entry, theme colors, background loading and tests), or a declarative list panel from PowerShell.
---

# Add a panel

Panels are Terminal.Gui v2 (2.5) windows run modally by `PanelHost` (`IPanelHost` service) on the REPL thread.
Each `Show` creates a fresh `IApplication` (instance model — never the static `Application.Init/Run`). A panel
hands a `PanelResult` back to the shell: `InsertText`, `ReplaceInput`, `RunCommand` or `ChangeDirectory`.
PanelHost catches every exception from a panel (logged, shown as one red line), restores the terminal, themes
every dialog and strips Terminal.Gui's terminal-title updates. Pickle.Tui references only Abstractions (+ Wizards).

## 1. The view: subclass `PanelWindow`

```csharp
namespace Pickle.Tui.Panels.Todo;

public sealed class TodoPanel : PanelWindow
{
    private readonly FilterableList<TodoItem> _list;
    private readonly PreviewPane _preview;

    public TodoPanel(PanelContext context) : base(context, "Todo")      // title shows as "Todo  ·  Esc to close"
    {
        _list = new FilterableList<TodoItem>(i => i.Title)
        {
            Width = Dim.Percent(50), Hint = i => i.Due, Category = i => i.Project, Schemes = Schemes,
        };
        _preview = new PreviewPane("Details") { X = Pos.Right(_list), Schemes = Schemes };
        Body.Add(_list, _preview);                                        // Body = area above the status bar

        _list.ItemAccepted += (_, item) => Complete(new PanelResult(PanelResultKind.InsertText, item.Id));
        _list.SelectionChanged += (_, item) => _preview.Show(item?.Title ?? "", item?.Notes.Split('\n') ?? []);
        AddHint(Key.F5, "Refresh", Load);                                 // status-bar hint + key
        _list.Filter.SetFocus();
    }

    internal FilterableList<TodoItem> List => _list;                     // for tests (InternalsVisibleTo)

    protected override void OnOpened() => Load();                        // App is available from here on

    private void Load() => RunInBackground(
        ct => Pickle.Shell.InvokeAsync("param($p) Get-Todo -Project $p", new Dictionary<string, object?> { ["p"] = "x" }, ShellTarget.Main, ct),
        result => _list.SetItems(result.Output.Select(TodoItem.From)),
        "loading…");
}
```

`PanelWindow` members (source-compatible; W7/W8/W9 code against them):

| Member | Use |
|---|---|
| `Context`, `Pickle`, `Body` | panel context, `IPickleContext`, content area |
| `AddHint(Key, text, action)` | status-bar shortcut |
| `Complete(PanelResult)` / `Close()` | finish with / without a result |
| `OpenPanel(id, argument)` | close and open another panel (its result wins) — used by the palette |
| `RunInBackground(work, onDone, busy)` | `Task.Run` + busy text in the title; errors → error box; token cancelled on close |
| `OnUi(action)` | marshal to the UI thread; queued until the window runs, dropped after it closed |
| `Every(interval, tick)` | UI-thread timer while the panel runs (e.g. Jobs refreshes every second) |
| `OnOpened()` | override: runs once the panel is running — start loading here |
| `Confirm`, `ShowError`, `ShowInfo`, `Prompt(title, label, initial)`, `Pick(title, items, text, hint)` | themed dialogs |
| `Schemes` (`PanelSchemes`), `Restyle()`, `PanelTitle`, `Lifetime`, `IsClosed`, `Hints` | theme, title, lifetime |

Never call git/winget/PowerShell-heavy work on the UI thread; use services (`Pickle.Services.Get<IGitService>()`) or
`Pickle.Shell.InvokeAsync(script, parameters, target)` and **pass user values as parameters** (for script text use
`[scriptblock]::Create($param)`), never by string concatenation.

## 2. Widgets (`src/Pickle.Tui/Widgets`)

- `FilterableList<T>` — fzf-style filter box + ranked list. Up/Down/PgUp/PgDn/Ctrl+N/P move, Enter raises
  `ItemAccepted`, Space marks when `MultiSelect` (`Chosen` = marked or selected). Optional `Category`, `Detail`,
  `Hint` (right-aligned, e.g. a chord), `Keywords` (extra searchable text), `ItemColor`. `SetItems`/`AddItems`
  (streaming), `Status` ("scanning…"). Large lists filter in the background. Match chars are highlighted.
- `FuzzyFilter.Match/Filter` — the single fuzzy-scoring seam (swap to `Pickle.Abstractions.FuzzyMatcher` there).
- `PreviewPane` — titled read-only pane: `Show(title, lines, lineNumbers)`, `ShowMessage`, `PreviewLine` colors.
- `PanelDialogs.Prompt/Pick` — themed modal text prompt and filterable pick list.
- `KeyHints.Describe(registry, action)` — "F1 · Ctrl+P" for display; `KeyHints.ToChord(Key)` — capture chords.
- `PanelStyle.For(theme)` → `PanelSchemes` (Base/List/Dialog/Error/Input/Border schemes plus Normal, Selected, Muted,
  Match, Accent, Success, Warning, ErrorText, Info attributes). Custom-drawn widgets implement `IThemedWidget` so
  live theme changes reach them. Colors always come from `Theme.Ui` — never hard-code.

## 3. Register it: a plugin

```csharp
public sealed class TodoPanelPlugin : IPicklePlugin
{
    public string Id => "pickle.todo";
    public string DisplayName => "Todo";
    public string Description => "Todo list panel.";

    public void Initialize(IPickleContext context) => context.Panels.Register(new PanelDescriptor
    {
        Id = "todo", Title = "Todo", Description = "Your todo list",
        DefaultKey = "Alt+T",                 // → action "panel.todo" + binding (unless the chord is taken)
        WindowsOnly = false,
        CreateView = ctx => new TodoPanel(ctx),
    });
}
```

Add the plugin to `src/Pickle/BuiltInPlugins.cs` (built-ins; W6's own panels are registered by `TuiPlugin`).
The palette (F1) lists every registered panel automatically with its chord. Extra editor actions:
`context.KeyBindings.RegisterAction(name, description, (buffer, ct) => { buffer.ShowPanel("todo", arg); ... })`;
default chords live in `Pickle.Core/Input/DefaultKeyBindings.cs`.

## 4. PowerShell list panels (no C#)

`Panels.RegisterList(new ListPanelSpec { Id, Title, ItemsScript, Actions = { ["Open"] = "..." } })` (from
`Register-PicklePanel`). `ListPanelSync` turns specs into descriptors at startup, before each prompt and on demand.
Items show their `Name` (else `ToString()`), details via `Format-List`; Enter runs the first action, F2/F3/F4/F6…
the others, with `$_` bound to the item. An action returning `@{ Run = '...' }` / `Insert` / `Replace` / `Cd`
completes the panel with that result; other output is shown in the details pane and the list refreshes.

## 5. Tests (`tests/Pickle.Tui.Tests`)

```csharp
var script = new UiScript()
    .WaitFor("loaded", app => TuiHarness.Top<TodoPanel>(app).List.TotalCount > 0)
    .Type("milk")
    .Press(Key.Enter);
var (t, host) = TuiHarness.Start(script);          // TestPickle + TuiPlugin + VirtualTimeProvider apps
using var _ = t;
t.Runtime.Panels.Register(new PanelDescriptor { ... });   // or load your plugin
var result = host.Show("todo");
script.AssertOk();
Assert.Equal(new PanelResult(PanelResultKind.InsertText, "42"), result);
```

- Steps run on the UI thread from the `Iteration` event; later steps keep running inside nested dialogs.
  In conditions right after a key that closes a dialog, use `app.TopRunnableView is X x && ...` (not `Top<X>`).
- Virtual time: `Every` timers don't tick by themselves — load in `OnOpened()` so tests see data.
- Don't change the shell location in parallel tests (it changes the process working directory).
- Windows-only services: use the fakes in `tests/Pickle.Testing/Fakes`.
- pty E2E: add `test_*` functions to `tests/Pickle.E2E/e2e_<area>.py` (`PickleSession`, `s.press("f1")`, ...).
  pyte has no alternate screen: assert on the prompt line and on `s.raw` bytes (see `e2e_w6_panels.py`).

Run: `scripts/check.sh --quick --filter Todo`, then `python3 tests/Pickle.E2E/run_e2e.py -k todo`, then `scripts/check.sh`.
