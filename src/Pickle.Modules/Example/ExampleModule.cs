using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Modules.Example;

/// <summary>
/// The reference optional module: copy this folder to start a new one (see docs/modules.md and the add-module skill).
/// It shows each extension point once: a <c>pk</c> command, a prompt segment, a hook, a panel, a typed setting and a
/// service other code can use. Hidden from <c>pk module list</c> (use <c>--all</c>).
/// </summary>
public sealed class ExampleModule : IPicklePlugin
{
    public const string ModuleId = "example";

    public static ModuleDescriptor Descriptor { get; } = new()
    {
        Id = ModuleId,
        Name = "Example module",
        Description = "A tiny reference module: pk hello, a prompt segment, a hook and a panel.",
        Create = () => new ExampleModule(),
        Hidden = true,
        Provides = ["pk hello", "segment: hello", "panel: hello (Alt+Shift+H)"],
    };

    // Convention: plugin ids are "pickle.<module id>".
    string IPicklePlugin.Id => "pickle." + ModuleId;

    public string DisplayName => Descriptor.Name;

    public string Description => Descriptor.Description;

    public void Initialize(IPickleContext context)
    {
        var greetings = new GreetingService(context.Config);
        context.Services.Add<IGreetingService>(greetings);

        context.Commands.Register(new HelloCommand(greetings));
        context.PromptSegments.Register(new HelloSegment(greetings));
        context.Panels.Register(HelloPanel.Descriptor);
        context.Commands.Register(new HelloPanelCommand());

        // Hooks run on every prompt and command; keep them fast and never throw.
        context.Hooks.Register(HookKind.PostExecute, (_, _) =>
        {
            greetings.CommandsRun++;
            return ValueTask.CompletedTask;
        });
    }
}

/// <summary>Stored under <c>extensions.example</c> in config.json: <c>pk config set extensions.example.greeting Hola</c>.</summary>
public sealed class ExampleSettings
{
    public string Greeting { get; set; } = "Hello";

    public bool ShowSegment { get; set; } = true;
}

/// <summary>A service registered by the module; other plugins reach it with <c>Services.Get&lt;IGreetingService&gt;()</c>.</summary>
public interface IGreetingService
{
    string Greet(string? name);

    int CommandsRun { get; }

    bool SegmentEnabled { get; }
}

internal sealed class GreetingService(IConfigStore config) : IGreetingService
{
    public int CommandsRun { get; set; }

    public bool SegmentEnabled => config.Get<ExampleSettings>(ExampleModule.ModuleId).ShowSegment;

    public string Greet(string? name) =>
        $"{config.Get<ExampleSettings>(ExampleModule.ModuleId).Greeting}, {(string.IsNullOrWhiteSpace(name) ? Environment.UserName : name.Trim())}!";
}
