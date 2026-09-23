using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Host;
using Pickle.Abstractions;

namespace Pickle.Core.Cmdlets;

/// <summary>
/// <c>pk &lt;command&gt; [args]</c> (aliases: pk, pickle). Dispatches to <see cref="IPickleCommand"/>s registered by
/// Core, workstreams and plugins. Output written from any thread is marshalled back to the pipeline thread.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "PickleCommand")]
[OutputType(typeof(PSObject))]
public sealed class InvokePickleCommandCmdlet : PickleCmdlet
{
    private readonly CancellationTokenSource _cts = new();

    [Parameter(Position = 0)]
    public string? Name { get; set; }

    [Parameter(Position = 1, ValueFromRemainingArguments = true)]
    public string[] Arguments { get; set; } = [];

    protected override void EndProcessing()
    {
        var runtime = Runtime;
        if (string.IsNullOrEmpty(Name) || Name is "help" or "-h" or "--help" or "-?" or "/?")
        {
            WriteHelp(runtime, Arguments.FirstOrDefault());
            return;
        }

        var command = runtime.CommandRegistry.Get(Name);
        if (command is null)
        {
            WriteError(new ErrorRecord(
                new ArgumentException($"Unknown command 'pk {Name}'. Run 'pk help' to list commands."),
                "PickleUnknownCommand",
                ErrorCategory.ObjectNotFound,
                Name));
            return;
        }

        var queue = new BlockingCollection<(int Kind, object? Payload)>();
        var context = new PickleCommandContext
        {
            Pickle = runtime,
            WriteObject = o => queue.Add((0, o)),
            WriteHost = s => queue.Add((1, s)),
            WriteError = s => queue.Add((2, s)),
            Confirm = (question, defaultYes) => Confirm(runtime, question, defaultYes),
            Interactive = runtime.Shell.IsInteractive,
            Cwd = SessionState.Path.CurrentFileSystemLocation.ProviderPath,
        };

        var task = Task.Run(() => command.ExecuteAsync(context, Arguments, _cts.Token).AsTask());
        while (!task.IsCompleted || queue.Count > 0)
        {
            if (queue.TryTake(out var item, 30))
            {
                Dispatch(item);
            }
        }

        int exitCode;
        try
        {
            exitCode = task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            exitCode = 130;
        }
        catch (Exception ex) when (ex is not PipelineStoppedException)
        {
            runtime.Log.Error("pk", $"pk {Name} failed", ex);
            WriteError(new ErrorRecord(ex, "PickleCommandFailed", ErrorCategory.NotSpecified, Name));
            exitCode = 1;
        }

        SessionState.PSVariable.Set("global:LASTEXITCODE", exitCode);
    }

    protected override void StopProcessing() => _cts.Cancel();

    private void Dispatch((int Kind, object? Payload) item)
    {
        switch (item.Kind)
        {
            case 0:
                WriteObject(item.Payload, enumerateCollection: true);
                break;
            case 1:
                Host.UI.WriteLine(item.Payload as string ?? string.Empty);
                break;
            default:
                WriteError(new ErrorRecord(new InvalidOperationException(item.Payload as string), "PickleCommandError", ErrorCategory.NotSpecified, Name));
                break;
        }
    }

    private static bool Confirm(PickleRuntime runtime, string question, bool defaultYes)
    {
        if (!runtime.Shell.IsInteractive)
        {
            return defaultYes;
        }

        var choices = new Collection<ChoiceDescription>
        {
            new("&Yes"),
            new("&No"),
        };
        return runtime.Engine.Host.UI.PromptForChoice(string.Empty, question, choices, defaultYes ? 0 : 1) == 0;
    }

    private void WriteHelp(PickleRuntime runtime, string? topic)
    {
        var theme = runtime.Themes.Current;
        if (topic is not null && runtime.CommandRegistry.Get(topic) is { } cmd)
        {
            Host.UI.WriteLine(Ansi.Colorize("pk " + cmd.Name, theme.Ui.Accent, bold: true) + " — " + cmd.Description);
            Host.UI.WriteLine("  " + cmd.Usage);
            return;
        }

        Host.UI.WriteLine(Ansi.Colorize("Pickle commands", theme.Ui.Accent, bold: true) + Ansi.Colorize("  (pk <command> --help for details)", theme.Ui.Muted));
        var commands = runtime.CommandRegistry.All.ToList();
        var width = commands.Count == 0 ? 8 : Math.Min(16, commands.Max(c => c.Name.Length) + 2);
        foreach (var c in commands)
        {
            Host.UI.WriteLine("  " + Ansi.Colorize(c.Name.PadRight(width), theme.Ui.Accent) + c.Description);
        }

        Host.UI.WriteLine(string.Empty);
        Host.UI.WriteLine(Ansi.Colorize("Keys: ", theme.Ui.Muted) + "F1 palette · Ctrl+R history · Ctrl+T files · F2 wizard · Alt+G git · Alt+W winget · Alt+U updates · Alt+J jobs · Alt+S scheduler · Alt+, settings");
    }
}
