# Pickle.Windows

Windows integrations behind Abstractions interfaces. Must compile on Linux (single `net10.0` TFM).

- Mark Windows-only types `[SupportedOSPlatform("windows")]`; public entry points check `OperatingSystem.IsWindows()`
  and report "unsupported" instead of throwing on other OSes.
- Privileged work goes through `IElevationBroker` (allowlisted `ElevatedOperationKind`s only — never arbitrary
  commands). The helper is `pickle.exe --elevated-helper <pipe> <nonce>`.
- External processes: `ProcessStartInfo.ArgumentList` only. Validate ids (winget ids, GUIDs) with strict regexes.
- Real-system tests are `[Fact(Skip = …)]`-guarded or use `Assert.Skip` on non-Windows; logic is tested with parsers
  and fakes on every OS.
