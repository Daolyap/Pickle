using System.Collections;
using System.Management.Automation;
using Pickle.Abstractions;
using Pickle.Core.Commands;
using Pickle.Core.Plugins;

namespace Pickle.Core.Cmdlets;

/// <summary>Shared plumbing for the <c>Register-Pickle*</c> cmdlets used by PowerShell plugins and profiles.</summary>
public abstract class PickleRegistrationCmdlet : PickleCmdlet
{
    protected PluginManager? Manager => Runtime.Plugins as PluginManager;

    /// <summary>The plugin doing the registering: the scriptblock's module, else the plugin being loaded.</summary>
    protected string? PluginId(ScriptBlock? block = null) => block?.Module?.Name ?? Manager?.LoadingPluginId;

    protected void Record(string what, ScriptBlock? block = null) => Manager?.RecordContribution(PluginId(block), what);

    protected void Fail(string message, string id, object? target) =>
        WriteError(new ErrorRecord(new ArgumentException(message), id, ErrorCategory.InvalidArgument, target));
}

/// <summary><c>Register-PicklePromptSegment -Type weather -ScriptBlock { param($context) '☀ 21°' }</c>.</summary>
[Cmdlet(VerbsLifecycle.Register, "PicklePromptSegment")]
public sealed class RegisterPicklePromptSegmentCmdlet : PickleRegistrationCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrEmpty]
    public string Type { get; set; } = string.Empty;

    /// <summary>Receives the PromptContext ($args[0] / $PickleContext); returns text, an object with Text/Foreground/Background, or $null to hide.</summary>
    [Parameter(Mandatory = true, Position = 1)]
    public ScriptBlock ScriptBlock { get; set; } = null!;

    protected override void EndProcessing()
    {
        Runtime.PromptSegmentRegistry.Register(new ScriptPromptSegment(Runtime, Type, ScriptBlock));
        Record("segment: " + Type, ScriptBlock);
    }
}

/// <summary><c>Register-PickleCompletion -Name deploy-targets -CommandName deploy -ScriptBlock { param($word) 'prod','staging' }</c>.</summary>
[Cmdlet(VerbsLifecycle.Register, "PickleCompletion")]
public sealed class RegisterPickleCompletionCmdlet : PickleRegistrationCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrEmpty]
    public string Name { get; set; } = string.Empty;

    /// <summary>Commands whose arguments this completes (wildcards allowed).</summary>
    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string[] CommandName { get; set; } = [];

    /// <summary>Receives (word, input, cursor); returns strings or objects with CompletionText/ListText/Description.</summary>
    [Parameter(Mandatory = true)]
    public ScriptBlock ScriptBlock { get; set; } = null!;

    protected override void EndProcessing()
    {
        Runtime.CompletionRegistry.Register(new ScriptCompletionProvider(Runtime, Name, CommandName, ScriptBlock));
        Record("completion: " + Name, ScriptBlock);
    }
}

/// <summary><c>Register-PickleWizard -Definition @{ id = 'mytool'; command = 'mytool'; sections = @(...) }</c> (hashtable, object or JSON).</summary>
[Cmdlet(VerbsLifecycle.Register, "PickleWizard")]
public sealed class RegisterPickleWizardCmdlet : PickleRegistrationCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    public object Definition { get; set; } = null!;

    protected override void ProcessRecord()
    {
        WizardDefinition wizard;
        try
        {
            wizard = PsJson.ToModel<WizardDefinition>(Definition);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            Fail($"Invalid wizard definition: {ex.Message}", "PickleWizardInvalid", Definition);
            return;
        }

        if (string.IsNullOrWhiteSpace(wizard.Id) || string.IsNullOrWhiteSpace(wizard.Command))
        {
            Fail("A wizard needs at least 'id' and 'command'.", "PickleWizardInvalid", Definition);
            return;
        }

        if (string.IsNullOrWhiteSpace(wizard.Title))
        {
            wizard.Title = wizard.Id;
        }

        Runtime.WizardRegistry.Register(wizard);
        Record("wizard: " + wizard.Id);
    }
}

/// <summary>
/// <c>Register-PickleKeyBinding -Chord Alt+x -Action panel.git</c>, or with a scriptblock that receives the current
/// input text and returns replacement text (or $null to leave it).
/// </summary>
[Cmdlet(VerbsLifecycle.Register, "PickleKeyBinding", DefaultParameterSetName = "Action")]
public sealed class RegisterPickleKeyBindingCmdlet : PickleRegistrationCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrEmpty]
    public string Chord { get; set; } = string.Empty;

    [Parameter(Mandatory = true, Position = 1, ParameterSetName = "Action")]
    [ValidateNotNullOrEmpty]
    public string Action { get; set; } = string.Empty;

    [Parameter(Mandatory = true, Position = 1, ParameterSetName = "Script")]
    public ScriptBlock ScriptBlock { get; set; } = null!;

    [Parameter(ParameterSetName = "Script")]
    public string? Description { get; set; }

    protected override void EndProcessing()
    {
        if (!KeyChord.TryParse(Chord, out var chord))
        {
            Fail($"'{Chord}' is not a valid key chord. Examples: Ctrl+R, Alt+G, F5, Ctrl+Shift+K, Alt+,", "PickleChordInvalid", Chord);
            return;
        }

        var registry = Runtime.KeyBindingRegistry;
        var runtime = Runtime;
        var actionName = Action;
        if (ParameterSetName == "Script")
        {
            var block = ScriptBlock;
            actionName = $"plugin.{PluginId(block) ?? "user"}.{chord}".ToLowerInvariant();
            registry.RegisterAction(actionName, Description ?? $"Script bound to {chord}", (buffer, cancellationToken) =>
            {
                var result = ScriptInvoker.InvokeAsync(
                    runtime,
                    block,
                    new Dictionary<string, object?> { ["PickleInput"] = buffer.Text },
                    [buffer.Text, buffer.Cursor],
                    cancellationToken).GetAwaiter().GetResult();
                ScriptInvoker.LogErrors(runtime, $"key binding {chord}", result);
                if (result.Output.LastOrDefault(o => o is not null)?.ToString() is { } text && text != buffer.Text)
                {
                    buffer.Replace(text, text.Length);
                }

                return ValueTask.CompletedTask;
            });
        }
        else if (registry.GetAction(Action) is null)
        {
            WriteWarning($"No action named '{Action}' is registered yet; the binding works once it is.");
        }

        registry.Bind(chord.ToString(), actionName);
        Record($"key: {chord} → {actionName}", ScriptBlock);
    }
}

/// <summary><c>Register-PickleCommand -Name hello -Description 'Say hi' -ScriptBlock { "hi $args" }</c> adds <c>pk hello</c>.</summary>
[Cmdlet(VerbsLifecycle.Register, "PickleCommand")]
public sealed class RegisterPickleCommandCmdlet : PickleRegistrationCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidatePattern("^[A-Za-z][A-Za-z0-9._-]*$")]
    public string Name { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    public string Description { get; set; } = string.Empty;

    [Parameter]
    public string? Usage { get; set; }

    /// <summary>Runs with the command's arguments in $args; output objects are written to the pipeline.</summary>
    [Parameter(Mandatory = true)]
    public ScriptBlock ScriptBlock { get; set; } = null!;

    /// <summary>Replace a command that isn't script-based (e.g. a built-in).</summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    protected override void EndProcessing()
    {
        if (Runtime.CommandRegistry.Get(Name) is { } existing and not ScriptPickleCommand && !Force)
        {
            Fail($"'pk {Name}' is already provided by Pickle or a .NET plugin. Use -Force to replace it.", "PickleCommandExists", Name);
            return;
        }

        Runtime.CommandRegistry.Register(new ScriptPickleCommand(Runtime, Name, Description, Usage ?? $"pk {Name} [args]", ScriptBlock));
        Record("command: " + Name, ScriptBlock);
    }
}

/// <summary>
/// <c>Register-PicklePanel -Id todo -Title 'Todo' -Items { Get-Content ~/todo.txt } -Actions @{ Done = { ... } }</c>:
/// a list panel (items from the scriptblock; each action runs with <c>$_</c> set to the selected item).
/// </summary>
[Cmdlet(VerbsLifecycle.Register, "PicklePanel")]
public sealed class RegisterPicklePanelCmdlet : PickleRegistrationCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidatePattern("^[A-Za-z][A-Za-z0-9._-]*$")]
    public string Id { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    public string Title { get; set; } = string.Empty;

    [Parameter]
    public string? Description { get; set; }

    /// <summary>Default chord, e.g. Alt+T.</summary>
    [Parameter]
    public string? Key { get; set; }

    [Parameter(Mandatory = true)]
    public ScriptBlock Items { get; set; } = null!;

    /// <summary>Label → scriptblock (or script text).</summary>
    [Parameter]
    public Hashtable? Actions { get; set; }

    protected override void EndProcessing()
    {
        if (Key is not null && !KeyChord.TryParse(Key, out _))
        {
            Fail($"'{Key}' is not a valid key chord.", "PickleChordInvalid", Key);
            return;
        }

        var actions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Actions ?? [])
        {
            var script = entry.Value is PSObject pso ? pso.BaseObject : entry.Value;
            actions[entry.Key.ToString() ?? string.Empty] = script?.ToString() ?? string.Empty;
        }

        Runtime.PanelRegistry.RegisterList(new ListPanelSpec
        {
            Id = Id,
            Title = Title,
            Description = Description ?? string.Empty,
            DefaultKey = Key,
            ItemsScript = Items.ToString(),
            Actions = actions,
        });
        Record("panel: " + Id, Items);
    }
}

/// <summary>
/// <c>Register-PickleTranslation -Name tac -Description '...' -ScriptBlock { ... }</c> adds a shim function;
/// <c>Register-PickleTranslation -Pattern '^ll$' -Replacement 'Get-ChildItem -Force'</c> adds a regex rewriter.
/// </summary>
[Cmdlet(VerbsLifecycle.Register, "PickleTranslation", DefaultParameterSetName = "Shim")]
public sealed class RegisterPickleTranslationCmdlet : PickleRegistrationCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ParameterSetName = "Shim")]
    [Parameter(ParameterSetName = "Rewrite")]
    [ValidateNotNullOrEmpty]
    public string Name { get; set; } = string.Empty;

    [Parameter(Mandatory = true, ParameterSetName = "Shim")]
    [Parameter(ParameterSetName = "Rewrite")]
    public string Description { get; set; } = string.Empty;

    [Parameter(Mandatory = true, ParameterSetName = "Shim")]
    public ScriptBlock ScriptBlock { get; set; } = null!;

    [Parameter(Mandatory = true, ParameterSetName = "Rewrite")]
    [ValidateNotNullOrEmpty]
    public string Pattern { get; set; } = string.Empty;

    [Parameter(Mandatory = true, ParameterSetName = "Rewrite")]
    [AllowEmptyString]
    public string Replacement { get; set; } = string.Empty;

    [Parameter(ParameterSetName = "Rewrite")]
    public int Order { get; set; } = 500;

    protected override void EndProcessing()
    {
        if (ParameterSetName == "Shim")
        {
            Runtime.TranslationRegistry.RegisterShim(new TranslationShim(Name, Description, ScriptBlock.ToString()));
            Record("shim: " + Name, ScriptBlock);
            return;
        }

        var name = string.IsNullOrEmpty(Name) ? Pattern : Name;
        try
        {
            Runtime.TranslationRegistry.RegisterRewriter(new RegexRewriter(name, Pattern, Replacement, Order));
            Record("rewriter: " + name);
        }
        catch (ArgumentException ex)
        {
            Fail($"Invalid regex '{Pattern}': {ex.Message}", "PickleRegexInvalid", Pattern);
        }
    }
}

/// <summary><c>Register-PickleHook -Event PostExecute -ScriptBlock { if (-not $PickleEvent.Success) { ... } }</c>.</summary>
[Cmdlet(VerbsLifecycle.Register, "PickleHook")]
public sealed class RegisterPickleHookCmdlet : PickleRegistrationCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public HookKind Event { get; set; }

    /// <summary>Runs with <c>$PickleEvent</c> (also $args[0]) describing the event.</summary>
    [Parameter(Mandatory = true, Position = 1)]
    public ScriptBlock ScriptBlock { get; set; } = null!;

    protected override void EndProcessing()
    {
        var runtime = Runtime;
        var block = ScriptBlock;
        var kind = Event;
        runtime.HookRegistry.Register(kind, async (hookEvent, cancellationToken) =>
        {
            var result = await ScriptInvoker.InvokeAsync(
                runtime,
                block,
                new Dictionary<string, object?> { ["PickleEvent"] = hookEvent },
                [hookEvent],
                cancellationToken).ConfigureAwait(false);
            ScriptInvoker.LogErrors(runtime, $"{kind} hook", result);
        });
        Record("hook: " + kind, block);
    }
}

/// <summary><c>Get-PicklePlugin [-Id pickle.*]</c>: loaded, disabled, untrusted and failed plugins.</summary>
[Cmdlet(VerbsCommon.Get, "PicklePlugin")]
[OutputType(typeof(PluginInfo))]
public sealed class GetPicklePluginCmdlet : PickleCmdlet
{
    [Parameter(Position = 0)]
    [SupportsWildcards]
    public string? Id { get; set; }

    protected override void EndProcessing()
    {
        if (Runtime.Plugins is not PluginManager manager)
        {
            return;
        }

        var pattern = WildcardPattern.Get(string.IsNullOrEmpty(Id) ? "*" : Id, WildcardOptions.IgnoreCase);
        foreach (var plugin in manager.Plugins.Where(p => pattern.IsMatch(p.Id)))
        {
            WriteObject(plugin);
        }
    }
}
