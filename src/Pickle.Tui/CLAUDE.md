# Pickle.Tui

Terminal.Gui v2 (2.5.x) full-screen panels. Depends only on Abstractions (+ Wizards).

- Every panel: `Panels/<Feature>/` with a `…PanelPlugin : IPicklePlugin` that registers a `PanelDescriptor`
  (Id, Title, Description, DefaultKey, `CreateView` returning a `Runnable`/`Window`).
- Use the instance model only (`view.App`, `Application.Create()`); close with `RequestStop()`; set
  `PanelContext.Result` to insert text / run a command / cd.
- Data comes from services (`context.Pickle.Services.Get<IGitService>()` etc.) — never call git/winget directly
  from a view. Long work: `Task.Run` + `App.Invoke(...)` to update the UI thread.
- Colors: `PanelStyle.For(pickle)` / `PanelWindow.Schemes` (from `Theme.Ui`); fuzzy filtering via `Widgets/FilterableList` (shared `FuzzyMatcher`). Recipe: `.claude/skills/add-panel`.
- Tests: `Application.Create(new VirtualTimeProvider())`, `app.Init()`, inject keys, fake services from Pickle.Testing.
