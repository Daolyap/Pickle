---
name: add-module
description: Add an optional module to Pickle (compiled in, off until selected at install, in pk setup or with pk module) - a ModuleDescriptor plus an IPicklePlugin in src/Pickle.Modules with commands, a ResourcePanel, prompt segments or hooks, installer entries and tests.
---

# Add an optional module

Read `docs/modules.md` first; `src/Pickle.Modules/Example/` is the template (every extension point once, tested).

## 1. The module class

```csharp
namespace Pickle.Modules.Docker;

public sealed class DockerModule : IPicklePlugin
{
    public const string ModuleId = "docker";

    public static ModuleDescriptor Descriptor { get; } = new()
    {
        Id = ModuleId, Name = "Docker", Description = "Containers, images and volumes panel and pk docker",
        Create = () => new DockerModule(),
        Platforms = ModulePlatforms.All, Tools = ["docker", "podman"], Provides = ["pk docker", "panel: docker (Alt+Shift+D)"],
    };

    string IPicklePlugin.Id => "pickle." + ModuleId;
    public string DisplayName => Descriptor.Name;
    public string Description => Descriptor.Description;

    public void Initialize(IPickleContext context)
    {
        var docker = new DockerService(context.Services.Require<IProgramRunner>(), context.Config);
        context.Services.Add<IDockerService>(docker);          // an interface in your folder, so tests can fake it
        context.Panels.Register(DockerPanel.Descriptor);
        context.Commands.Register(new DockerCommand());
    }
}
```

`Initialize` runs at startup for enabled modules only. Keep it to registrations: no process starts, no file reads.

## 2. Structure

- `…Service.cs`: the logic over `IProgramRunner` (parse CLI output with `--format json` where it exists), returning
  records. No UI types. One interface so the panel and tests can use a fake.
- `…Panel.cs`: `ResourcePanel<T>`: `LoadAsync`, `DescribeAsync`, `AddAction` (background action then refresh, with
  `confirm:` for destructive ones), `AddCommand` (close and run a streaming/interactive command in the shell).
- `…Command.cs`: `PanelCommand` (panel when interactive, objects when scripted) or `PickleCommandBase`.
- Settings: a class under `extensions.<id>` (`context.Config.Get<T>(id)`); document each key in the module's `Provides`/docs.
- A missing tool: `runner.Find("docker") is null` → one line saying how to install it (Windows: `pk tool install`), exit code 1.

## 3. Register and package

1. `src/Pickle.Modules/OptionalModules.cs`: add `DockerModule.Descriptor`.
2. `packaging/modules.json`: add `{ "id", "name", "description" }` (the drift test in `tests/Pickle.Modules.Tests` fails otherwise;
   MSI features and RPM sub-packages are generated from it).
3. `docs/modules.md` table row; `README.md` feature table if it is user-visible.

## 4. Tests (`tests/Pickle.Modules.Tests/<Name>/`)

```csharp
var runner = new FakeProgramRunner().On("docker", "ps", "[{...json...}]");
var (t, host) = TuiHarness.Start(script, modules: OptionalModules.All, machineModules: ["docker"]);
t.Runtime.Services.Add<IProgramRunner>(runner);     // replace the real runner BEFORE the panel opens
```

- Parsers: pure functions with real captured output as test data.
- Command: `t.Run("pk docker list | ForEach-Object Name")`.
- Panel: `UiScript` + `TuiHarness.Start(...)` as in `ExampleModuleTests`.
- Every external call is asserted through `runner.CommandLines("docker")`, including that user values are single arguments.

Run `scripts/check.sh --quick --filter Docker`, then `scripts/check.sh`.
