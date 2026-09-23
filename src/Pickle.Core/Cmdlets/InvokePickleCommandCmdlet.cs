using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Host;
using Pickle.Abstractions;
using Pickle.Core.Commands;

namespace Pickle.Core.Cmdlets;

/// <summary>
/// <c>pk &lt;command&gt; [args]</c> (aliases: pk, pickle). Dispatches to <see cref="IPickleCommand"/>s registered by
/// Core, workstreams and plugins. The command starts on the pipeline thread (so it can run PowerShell nested);
/// output and runspace work from other threads is marshalled back through <see cref="PkInvocation"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "PickleCommand")]
[OutputType(typeof(PSObject))]
public sealed class InvokePickleCommandCmdlet : PickleCmdlet
{
    private static readonly string[] HelpFlags = ["--help", "-h", "-?", "/?"];
    private readonly CancellationTokenSource _cts = new();

    [Parameter(Position = 0)]
    public string? Name { get; set; }

    [Parameter(Position = 1, ValueFromRemainingArguments = true)]
    public string[] Arguments { get; set; } = [];

    protected override void EndProcessing()
    {
        var runtime = Runtime;
        if (string.IsNullOrEmpty(Name) || Name is "help" || HelpFlags.Contains(Name))
        {
            WriteHelp(runtime, Name is "help" ? Arguments.FirstOrDefault() : null);
            return;
        }

        var command = runtime.CommandRegistry.Get(Name);
        if (command is null)
        {
            var suggestions = Fuzzy.Suggest(Name, runtime.CommandRegistry.All.Select(c => c.Name));
            var hint = suggestions.Count > 0
                ? $" Did you mean {string.Join(" or ", suggestions.Select(s => $"'pk {s}'"))}?"
                : " Run 'pk help' to list commands.";
            WriteError(new ErrorRecord(
                new ArgumentException($"Unknown command 'pk {Name}'.{hint}"),
                "PickleUnknownCommand",
                ErrorCategory.ObjectNotFound,
                Name));
            SessionState.PSVariable.Set("global:LASTEXITCODE", 1);
            return;
        }

        if (Arguments.Length > 0 && (HelpFlags.Contains(Arguments[0]) || HelpFlags.Contains(Arguments[^1])))
        {
            WriteCommandHelp(runtime, command);
            return;
        }

        SessionState.PSVariable.Set("global:LASTEXITCODE", Execute(runtime, command));
    }

    protected override void StopProcessing() => _cts.Cancel();

    private int Execute(PickleRuntime runtime, IPickleCommand command)
    {
        var invocation = new PkInvocation();
        var context = new PickleCommandContext
        {
            Pickle = runtime,
            WriteObject = o => OnPipeline(invocation, () => WriteObject(o, enumerateCollection: true)),
            WriteHost = s => OnPipeline(invocation, () => Host.UI.WriteLine(s ?? string.Empty)),
            WriteError = s => OnPipeline(invocation, () => WriteError(new ErrorRecord(new InvalidOperationException(s), "PickleCommandError", ErrorCategory.NotSpecified, Name))),
            Confirm = (question, defaultYes) => invocation.TryRun(() => Confirm(runtime, question, defaultYes))?.GetAwaiter().GetResult() ?? defaultYes,
            Interactive = runtime.Shell.IsInteractive,
            Cwd = SessionState.Path.CurrentFileSystemLocation.ProviderPath,
        };

        Task<int> task;
        using (PkInvocation.Enter(invocation))
        {
            try
            {
                task = command.ExecuteAsync(context, Arguments, _cts.Token).AsTask();
            }
            catch (Exception ex) when (ex is not PipelineStoppedException)
            {
                task = Task.FromException<int>(ex);
            }
        }

        invocation.Pump(task);
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception ex) when (ex is not PipelineStoppedException)
        {
            runtime.Log.Error("pk", $"pk {Name} failed", ex);
            var message = ex is Config.ConfigValidationException or ArgumentException or InvalidOperationException ? ex.Message : $"pk {Name} failed: {ex.Message}";
            WriteError(new ErrorRecord(new InvalidOperationException(message, ex), "PickleCommandFailed", ErrorCategory.NotSpecified, Name));
            return 1;
        }
    }

    private static void OnPipeline(PkInvocation invocation, Action action)
    {
        if (invocation.IsPipelineThread)
        {
            action();
        }
        else
        {
            invocation.TryPost(action);
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

    private void WriteCommandHelp(PickleRuntime runtime, IPickleCommand command)
    {
        var theme = runtime.Themes.Current;
        Host.UI.WriteLine(Ansi.Colorize("pk " + command.Name, theme.Ui.Accent, bold: true) + " — " + command.Description);
        var usage = command.Usage.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (usage.Length > 0)
        {
            Host.UI.WriteLine(Ansi.Colorize("Usage:", theme.Ui.Muted));
            foreach (var line in usage)
            {
                Host.UI.WriteLine("  " + line);
            }
        }
    }

    private void WriteHelp(PickleRuntime runtime, string? topic)
    {
        var theme = runtime.Themes.Current;
        if (topic is not null)
        {
            if (runtime.CommandRegistry.Get(topic) is { } cmd)
            {
                WriteCommandHelp(runtime, cmd);
                return;
            }

            var suggestions = Fuzzy.Suggest(topic, runtime.CommandRegistry.All.Select(c => c.Name));
            Host.UI.WriteLine(Ansi.Colorize($"No command named '{topic}'.", theme.Ui.Warning)
                + (suggestions.Count > 0 ? $" Did you mean {string.Join(" or ", suggestions.Select(s => $"'{s}'"))}?" : string.Empty));
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
