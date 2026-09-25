# Writing Pickle plugins

Plugins add things to Pickle: `pk` commands, panels, command wizards, prompt segments, completions, key bindings,
Linux-style commands and hooks. There are two kinds, and both can do everything below:

| | PowerShell module plugin | .NET plugin |
|---|---|---|
| Written in | PowerShell (`.psm1` + `.psd1`) | C# (any .NET language), `net10.0` |
| Good for | Scripts, quick tools, list panels, wizards around your own functions | Rich panels (full Terminal.Gui UI), heavy logic, services |
| Loads | Automatically | Only after you **trust** its exact build (`pk plugin trust`) |
| Start with | `pk plugin new MyTools` | `pk plugin new MyTools --dotnet` |

Everything Pickle ships (the git, winget, sandbox and network tools panels, the wizards, the Windows integrations) is
built as plugins through the same interfaces, so anything built in is something a plugin can do too.

- [Quick start](#quick-start)
- [PowerShell plugins](#powershell-plugins): [commands](#pk-commands), [panels](#panels), [wizards](#wizards),
  [prompt segments](#prompt-segments), [completions](#completions), [key bindings](#key-bindings),
  [translations](#linux-style-commands), [hooks](#hooks)
- [.NET plugins](#net-plugins): [project](#project), [commands](#commands-in-c), [panels](#panels-in-c),
  [wizards and the rest](#everything-else-in-c)
- [Installing, sharing and trust](#installing-sharing-and-trust)
- [Testing and debugging](#testing-and-debugging)

## Quick start

```powershell
cd ~/src
pk plugin new MyTools          # a PowerShell module plugin in ./MyTools
pk plugin install ./MyTools    # copy it into Pickle's plugins folder
pk reload                      # load it without restarting
pk mytools                     # the sample command it registers
```

The scaffold registers a `pk` command, a prompt segment and a hook; edit `MyTools.psm1`, copy it again with
`pk plugin install ./MyTools` and restart Pickle to see changes.
`pk plugin list` shows every plugin, where it came from and whether it loaded (errors are in the log: `pk paths`).

## PowerShell plugins

A plugin is an ordinary PowerShell module whose manifest has a `Pickle` entry in `PrivateData`:

```powershell
# MyTools.psd1
@{
    RootModule        = 'MyTools.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = '5b1f…'            # New-Guid
    Author            = 'You'
    Description       = 'My Pickle tools'
    PowerShellVersion = '7.4'
    FunctionsToExport = @('Get-Thing')
    PrivateData       = @{
        Pickle = @{ Id = 'mytools' }       # this key is what makes it a Pickle plugin
        PSData = @{ Tags = @('Pickle') }
    }
}
```

Pickle imports the module at startup; `pk reload` picks up plugins installed since (restart Pickle after editing
one that is already loaded). The `Register-Pickle*` calls at the top level of the
`.psm1` run then; everything a plugin registers is tracked and removed when the plugin is disabled or reloaded.
Scriptblocks run in the main runspace, so they can call the module's own functions and see the user's session.

Plugins are found in the plugins folder (`pk paths` → Plugins, one folder per plugin), in modules installed with
`pk plugin install <name>` from the PowerShell Gallery, and in any module on `$env:PSModulePath` whose manifest has
`PrivateData.Pickle`.

### pk commands

```powershell
Register-PickleCommand -Name deploy -Description 'Deploy a site' -Usage 'pk deploy <site> [--dry-run]' -ScriptBlock {
    $site = $args[0]
    if (-not $site) { throw 'Name a site.' }
    Invoke-MyDeploy -Site $site -DryRun:($args -contains '--dry-run')
}
```

Arguments arrive in `$args` exactly as typed (`--dry-run` is not treated as a PowerShell parameter). Output objects go to the
pipeline, so `pk deploy web | Where-Object Status -eq Failed` works. `pk help` lists the command under "More" and
`pk deploy --help` prints its usage. `-Force` replaces an existing command of the same name.

### Panels

`Register-PicklePanel` builds a full-screen list panel from a script: a fuzzy-filterable list on the left, details on
the right, and actions on the F-keys.

```powershell
Register-PicklePanel -Id services -Title 'Services' -Key 'Alt+F9' -Command services `
    -Description 'Windows services: start, stop, restart' `
    -Items   { Get-Service | Sort-Object Status, DisplayName } `
    -Preview { $_ | Format-List Name, DisplayName, Status, StartType, DependentServices | Out-String } `
    -RefreshSeconds 5 `
    -Actions @{
        Start   = { Start-Service -Name $_.Name -PassThru }
        Stop    = { Stop-Service -Name $_.Name -PassThru }
        Restart = { Restart-Service -Name $_.Name -PassThru }
        Logs    = { @{ Run = "Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager' } -MaxEvents 50" } }
    }
```

| Parameter | What it does |
|---|---|
| `-Items` | Returns the rows. Each row shows its `Name` property (or the string itself). |
| `-Actions` | Label → scriptblock, run with `$_` = the selected row. Enter runs the first; the others get F2, F3, F4, F6… |
| `-Preview` | Text for the details pane (`$_` = selected row). Default: all its properties. |
| `-RefreshSeconds` | Reload the rows on a timer while the panel is open — a live dashboard. F5 always reloads. |
| `-Key` | A key chord that opens the panel. It also appears in the command palette (F1). |
| `-Command` | Also registers `pk <name>`, which opens the panel. |

What an action returns decides what happens next:

| Return | Result |
|---|---|
| `@{ Run = 'cmd' }` | Close the panel and run `cmd` at the prompt |
| `@{ Insert = 'text' }` | Close and insert `text` at the cursor |
| `@{ Replace = 'text' }` | Close and replace the whole input line |
| `@{ Cd = 'path' }` | Close and change directory |
| anything else | Shown in the details pane; the list reloads |

For a panel beyond a list (forms, tables, charts), write a [.NET plugin](#panels-in-c).

### Wizards

A wizard is a form that builds a command line (the F2 key opens the wizard for whatever command is on the prompt,
already filled in from what you typed). Its target can be any program **or your own PowerShell function**, which
makes wizards the quickest way to give a script a form.

```powershell
Register-PickleWizard -Definition @{
    id          = 'deploy'
    command     = 'Invoke-MyDeploy'
    title       = 'Deploy a site'
    description = 'Build and deploy one of our sites.'
    sections    = @(
        @{
            title   = 'Target'
            options = @(
                @{ id = 'site'; label = 'Site'; type = 'choice'; flag = '-Site'; required = $true
                   choices = @(@{ value = 'web' }, @{ value = 'api'; label = 'Public API' }) }
                @{ id = 'env'; label = 'Environment'; type = 'choice'; flag = '-Environment'; default = 'staging'
                   choices = @(@{ value = 'staging' }, @{ value = 'production'; warning = 'Deploys to customers.' }) }
                @{ id = 'dry'; label = 'Dry run'; type = 'flag'; flag = '-DryRun' }
                @{ id = 'notes'; label = 'Release notes'; type = 'path'; flag = '-NotesFile'; dependsOn = 'site' }
            )
        }
    )
    presets = @(
        @{ name = 'Staging dry run'; description = 'Try it without deploying'; values = @{ site = 'web'; env = 'staging'; dry = 'true' } }
    )
}
```

The definition can be a hashtable, an object or JSON (`Get-Content deploy.json -Raw | Register-PickleWizard`). You
can also drop JSON files into the wizards folder (`pk paths` → Wizards) without writing a plugin at all.

| Option `type` | Form field | Emitted as |
|---|---|---|
| `flag` | Checkbox | `flag` when ticked |
| `text`, `number` | Text box | `flag value` (`valueStyle`: `space`, `equals` → `--out=v`, `none` → `-ov`) |
| `path` | Text box with a file picker | same, quoted as needed |
| `choice` | Drop-down of `choices` | same |
| `list` | Multi-line box | `flag` once per line |
| `keyValueList` | Multi-line `key=value` box | `flag 'key<sep>value'` per line (`keyValueSeparator`) |
| `positional` | Text box | bare, ordered by `position` |

Other option fields: `required`, `default`, `placeholder`, `validation` (regex), `dependsOn` (only shown and
emitted when another option is set), `warning` (shown when set — for destructive flags), `description` (help line),
`template` (several placeholders in one value, e.g. a port forward `{local}:{host}:{remote}`), `raw` (a PowerShell
expression emitted unquoted). A wizard can have `modes` (subcommands such as `git commit` / `git log`, each with
its own sections), `aliases` (other command names it answers to), `wingetId` (offers to install the tool when it's
missing) and `windowsOnly`. The built-in definitions in `src/Pickle.Wizards/Definitions/*.json` are good examples.

### Prompt segments

```powershell
Register-PicklePromptSegment -Type k8sns -ScriptBlock {
    param($Context)                       # Cwd, LastCommandSucceeded, LastExitCode, IsAdmin, Now, TerminalWidth…
    $ns = kubectl config view --minify -o 'jsonpath={..namespace}' 2>$null
    if ($ns) { "⎈ $ns" }                  # return nothing to hide the segment
}
```

Add `{ "type": "k8sns" }` to the `left` or `right` segment list of your theme to show it (themes are JSON files in
the themes folder, `pk paths`; copy a built-in one there under a new name to customise it, then `pk theme set`). Segments
are rendered with a time budget and cached, so a slow segment never blocks the prompt.

### Completions

```powershell
Register-PickleCompletion -Name deploy-sites -CommandName Invoke-MyDeploy, deploy -ScriptBlock {
    param($WordToComplete, $Line, $Cursor)
    'web', 'api', 'docs' | Where-Object { $_ -like "$WordToComplete*" }
}
```

Return strings, or objects with `CompletionText`, `ListText` and `Description`. Suggestions appear in the Tab menu
next to PowerShell's own.

### Key bindings

```powershell
Register-PickleKeyBinding -Chord 'Alt+k' -Action panel.services          # any action or panel.<id>
Register-PickleKeyBinding -Chord 'Ctrl+Alt+d' -Description 'Prefix with a dry run' -ScriptBlock {
    param($Line, $Cursor)                 # the current input; return the new text, or $null to leave it
    "$Line -WhatIf"
}
```

Settings → Key bindings lists every action name.

### Linux-style commands

```powershell
# A shim: a function Pickle adds to the session (shown in pk translate)
Register-PickleTranslation -Name tac -Description 'Print lines in reverse' -ScriptBlock {
    param([string[]] $Path) $lines = Get-Content $Path; [array]::Reverse($lines); $lines
}

# A rewrite of the typed line before it runs (regex; the translated command is shown dimmed)
Register-PickleTranslation -Name dfh -Description 'df -h' -Pattern '^df -h$' -Replacement 'Get-PSDrive -PSProvider FileSystem'
```

### Hooks

```powershell
Register-PickleHook -Event PostExecute -ScriptBlock {
    # $PickleEvent: Kind, CommandLine, Cwd, PreviousCwd, Success, Duration
    if ($PickleEvent.Duration.TotalSeconds -gt 30) { [console]::Beep() }
}
```

Events: `PreExecute`, `PostExecute`, `Prompt`, `DirectoryChanged`, `Exit`. Keep hooks fast; they run on every prompt.

## .NET plugins

### Project

`pk plugin new MyTools --dotnet` creates this layout:

```xml
<!-- MyTools.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- The contracts. ExcludeAssets=runtime: the host's copy is used at run time. -->
    <PackageReference Include="Pickle.Abstractions" Version="0.1.*" ExcludeAssets="runtime" />
    <!-- Only for panels: the host's Terminal.Gui is used at run time (keep the same 2.x version). -->
    <PackageReference Include="Terminal.Gui" Version="2.5.*" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

```json
// plugin.json, next to the DLL
{ "id": "mytools", "assembly": "MyTools.dll" }
```

`Pickle.Abstractions` is attached to every GitHub release as `Pickle.Abstractions.<version>.nupkg` (and pushed to
nuget.org when the maintainers enable it): save it in a folder and add that folder as a package source with
`dotnet nuget add source <folder>`. Each plugin loads in
its own `AssemblyLoadContext` with its own dependencies; Pickle.Abstractions, PowerShell (`System.Management.Automation`)
and Terminal.Gui are shared with the host so types match.

```csharp
using Pickle.Abstractions;

public sealed class MyToolsPlugin : IPicklePlugin
{
    public string Id => "mytools";
    public string DisplayName => "My tools";
    public string Description => "Deploys, dashboards and the rest";

    public void Initialize(IPickleContext context)
    {
        context.Commands.Register(new DeployCommand());
        context.Panels.Register(DeployPanel.Descriptor);
        context.Wizards.Register(DeployWizard.Definition);
    }
}
```

`IPickleContext` is everything a plugin can reach: `Commands`, `Panels`, `Wizards`, `PromptSegments`,
`Completions`, `KeyBindings`, `Translations`, `Hooks`, `Aliases`, `History`, `Config`, `Themes`, `Shell` (run
PowerShell, insert text, submit commands), `Services` (shared services such as `IWingetService`, `IGitService`,
`ISandboxService`, `IToolInstaller`, `INetworkMonitor`, `IPanelHost` — or add your own), `Paths` and `Log`.

### Commands in C#

Derive from `PickleCommandBase` for argument parsing, themed output and error handling for free:

```csharp
internal sealed class DeployCommand : PickleCommandBase
{
    public override string Name => "deploy";
    public override string Description => "Deploy a site";
    public override string Usage => "pk deploy <site> [--env staging|production] [--dry-run]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken ct)
    {
        var args = CommandArgs.Parse(raw, "env");           // options that take a value
        if (args.Arg(0) is not { } site) return UsageError(output, "Name a site.");
        if (args.Value("env") == "production" && !output.Confirm(args, $"Deploy {site} to production?", false)) return 1;

        output.Muted($"Deploying {site}…");
        var result = await Deployer.RunAsync(site, args.Value("env") ?? "staging", args.Has("dry-run"), ct);
        output.Object(Display.Columns(result, "Site", "Version", "Status"));   // a table for scripts and people
        output.Success("Done.");
        return 0;
    }
}
```

`ArgumentException`s become usage errors (exit code 2); I/O, timeout and invalid-operation errors become exit code
1 with the message printed. `CommandOutput` has `Line`, `Heading`, `Muted`, `Success`, `Failure`, `Warning`,
`Status` (a line that the next one replaces) and `Transcript`. Write objects with `Object`; `Display.Columns` picks
which properties PowerShell shows while scripts still see all of them.

### Panels in C#

A panel is any Terminal.Gui v2 `Runnable` (usually a `Window`) created by a `PanelDescriptor`:

```csharp
using Pickle.Abstractions;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

internal sealed class DeployPanel : Window
{
    public static PanelDescriptor Descriptor { get; } = new()
    {
        Id = "deploy",
        Title = "Deploy",
        Description = "Pick a site and deploy it",
        DefaultKey = "Alt+F8",                    // also listed in the command palette (F1)
        CreateView = context => new DeployPanel(context),
    };

    public DeployPanel(PanelContext context)
    {
        Title = "Deploy  ·  Esc to close";
        var sites = new ListView { Width = 30, Height = Dim.Fill() };
        sites.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(["web", "api", "docs"]));
        var go = new Button { Text = "_Deploy", X = Pos.Right(sites) + 2 };
        go.Accepting += (_, e) =>
        {
            e.Handled = true;
            // Hand the shell something to do, then close.
            context.Result = new PanelResult(PanelResultKind.RunCommand, $"pk deploy {sites.SelectedItem switch { 1 => "api", 2 => "docs", _ => "web" }}");
            App?.RequestStop();
        };
        Add(sites, go);
    }
}
```

Pickle opens it in the alternate screen, applies the theme's colors, hands keys to it and restores the prompt
afterwards. Set `context.Result` to `RunCommand`, `InsertText`, `ReplaceInput` or `ChangeDirectory` before closing.
Guidelines that keep plugin panels feeling like the built-in ones:

- Load data off the UI thread (`Task.Run`) and update views through `App.Invoke(...)`.
- Get data from services (`context.Pickle.Services.Get<IGitService>()`, your own) or `context.Pickle.Shell.InvokeAsync`
  (with parameters, never string-built scripts), not by starting processes from the view.
- Put actions on F-keys and show them in a `StatusBar`; Esc closes.
- `context.Argument` is whatever followed the panel id when it was opened (`pk tools scan 10.0.0.0/24`).

Register `pk <name>` to open it with `IPanelHost`:

```csharp
context.Commands.Register(new OpenMyPanel());   // ExecuteAsync: context.Pickle.Services.Get<IPanelHost>()?.Show("deploy", arg)
```

### Everything else in C#

| Add | How |
|---|---|
| Wizard | `context.Wizards.Register(new WizardDefinition { Id = …, Command = …, Sections = [...] })` (same schema as above) |
| Prompt segment | implement `IPromptSegment` (`Type`, `RenderAsync(PromptContext, SegmentStyle, ct)`) → `context.PromptSegments.Register` |
| Completion | implement `ICompletionProvider` → `context.Completions.Register` |
| Key binding | `context.KeyBindings.RegisterAction(name, description, handler)` then `context.KeyBindings.Bind("Alt+X", name)` |
| Translation | `context.Translations.RegisterRewriter(...)` / `RegisterShim(...)` |
| Hook | `context.Hooks.Register(HookKind.PostExecute, (e, ct) => …)` (returns an `IDisposable` to unregister) |
| Service | `context.Services.Add<IMyService>(impl)`; other plugins get it with `Services.Get<IMyService>()` |
| Settings | read `context.Config.Current`; store your own file under `context.Paths.DataDir` |

Windows-only code: mark it `[SupportedOSPlatform("windows")]` and check `OperatingSystem.IsWindows()` so the plugin
still loads elsewhere.

## Installing, sharing and trust

```text
pk plugin install MyTools                  # from the PowerShell Gallery
pk plugin install https://github.com/me/pickle-mytools.git
pk plugin install ./MyTools                # a folder (a module, or a .NET build output with plugin.json)
pk plugin list | enable <id> | disable <id> | remove <id>
pk plugin trust <id>                       # .NET plugins only
```

- **PowerShell plugins** publish like any module: `Publish-PSResource -Path ./MyTools -ApiKey …`. The
  `PrivateData.Pickle` key is what makes Pickle load it; add the `Pickle` tag so people can find it
  (`Find-PSResource -Tag Pickle`).
- **.NET plugins** run native code, so Pickle loads one only after `pk plugin trust <id>`, which pins the SHA-256
  of that exact assembly. A rebuilt or updated DLL is not trusted until you trust it again. Ship the build output
  plus `plugin.json` as a zip or a git repository.
- Disabled plugins are skipped at startup.

## Testing and debugging

- `pk plugin list` shows load errors, and the log (`pk paths` → Logs) has the details. `pk reload` loads plugins
  installed since startup.
- Try commands headless: `pickle -NoLogo -c 'pk deploy web --dry-run'`, or pipe lines into `pickle --headless`.
- For .NET plugins, `tests/fixtures/SampleDotnetPlugin` in the Pickle repository is a minimal plugin, and
  `tests/Pickle.Testing` has `TestPickle` (an isolated runtime with a virtual terminal) and fakes for every Windows
  service — the same tools Pickle's own tests use.
- Set `PICKLE_HOME` to a scratch folder to try a plugin without touching your real config.
