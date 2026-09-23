# Pickle.Abstractions

Public contracts shared by every project and by third-party .NET plugins (this assembly ships as a NuGet package).

- Only dependency: System.Management.Automation (for PSObject/ErrorRecord in `ShellResult`). No UI libraries.
- Additive changes only: add members with defaults; never rename/remove without updating every implementer
  (Core registries, `tests/Pickle.Testing/Fakes`).
- Models (config, theme, wizard, service DTOs) serialize with `PickleJson.Options` (camelCase, enums as strings).
