using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Text;
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

    // No named parameters on purpose: `pk history list -n 5` must not bind -n to a cmdlet parameter. The pk/pickle
    // aliases go through the Invoke-Pickle function (Pickle.psm1), which also keeps -v/-d away from common parameters.
    [Parameter(ValueFromRemainingArguments = true)]
    public string[] Arguments { get; set; } = [];

    private string? Name => Arguments.Length > 0 ? Arguments[0] : null;

    private string[] CommandArguments => Arguments.Length > 1 ? Arguments[1..] : [];

    protected override void EndProcessing()
    {
        var runtime = Runtime;
        if (string.IsNullOrEmpty(Name) || Name is "help" || HelpFlags.Contains(Name))
        {
            WriteHelp(runtime, Name is "help" ? CommandArguments.FirstOrDefault() : null);
            return;
        }

        var command = runtime.CommandRegistry.Get(Name);
        if (command is null)
        {
            var suggestions = DidYouMean.Suggest(Name, runtime.CommandRegistry.All.Select(c => c.Name));
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

        var args = CommandArguments;
        if (args.Length > 0 && (HelpFlags.Contains(args[0]) || HelpFlags.Contains(args[^1])))
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
                task = command.ExecuteAsync(context, CommandArguments, _cts.Token).AsTask();
            }
            catch (Exception ex) when (ex is not PipelineStoppedException)
            {
                task = Task.FromException<int>(ex);
            }
        }

        try
        {
            invocation.Pump(task);
        }
        catch
        {
            // The pipeline is going away (Ctrl+C, or an error under -ErrorAction Stop): stop the command too.
            _cts.Cancel();
            throw;
        }

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

            var suggestions = DidYouMean.Suggest(topic, runtime.CommandRegistry.All.Select(c => c.Name));
            Host.UI.WriteLine(Ansi.Colorize($"No command named '{topic}'.", theme.Ui.Warning)
                + (suggestions.Count > 0 ? $" Did you mean {string.Join(" or ", suggestions.Select(s => $"'{s}'"))}?" : string.Empty));
        }

        var width = Math.Max(40, runtime.Terminal.Width - 1);
        Host.UI.WriteLine(Ansi.Colorize("Pickle commands", theme.Ui.Accent, bold: true) + Ansi.Colorize("  pk <command> --help for details", theme.Ui.Muted));
        var commands = runtime.CommandRegistry.All.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var nameWidth = commands.Count == 0 ? 8 : Math.Min(14, commands.Max(c => c.Name.Length)) + 2;
        var descriptionWidth = Math.Max(20, width - 2 - nameWidth);
        foreach (var (section, members) in HelpSections)
        {
            var inSection = commands.Where(c => members.Length == 0
                ? !HelpSections.Any(s => s.Members.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
                : members.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            if (inSection.Count == 0)
            {
                continue;
            }

            Host.UI.WriteLine(string.Empty);
            Host.UI.WriteLine(Ansi.Colorize(section, theme.Ui.Muted, bold: true));
            foreach (var c in inSection)
            {
                var lines = WrapWords(c.Description, descriptionWidth);
                Host.UI.WriteLine("  " + Ansi.Colorize(TextWidth.PadRight(c.Name, nameWidth), theme.Ui.Accent) + lines[0]);
                foreach (var more in lines.Skip(1))
                {
                    Host.UI.WriteLine(new string(' ', 2 + nameWidth) + more);
                }
            }
        }

        var keys = KeyHints(runtime);
        if (keys.Count == 0)
        {
            return;
        }

        Host.UI.WriteLine(string.Empty);
        Host.UI.WriteLine(Ansi.Colorize("Keys", theme.Ui.Muted, bold: true));
        var chordWidth = keys.Max(k => k.Chord.Length) + 1;
        var cellWidth = chordWidth + keys.Max(k => TextWidth.VisibleWidth(k.Label)) + 3;
        var columns = Math.Max(1, (width - 2) / cellWidth);
        for (var i = 0; i < keys.Count; i += columns)
        {
            var row = keys.Skip(i).Take(columns)
                .Select(k => Ansi.Colorize(k.Chord.PadRight(chordWidth), theme.Ui.Accent) + TextWidth.PadRight(k.Label, cellWidth - chordWidth));
            Host.UI.WriteLine("  " + string.Concat(row).TrimEnd());
        }
    }

    private static readonly (string Section, string[] Members)[] HelpSections =
    [
        ("Shell", ["history", "translate", "wizard", "git", "alias", "theme", "config", "reload"]),
        ("Windows", ["winget", "upgrade", "update", "schedule", "terminal"]),
        ("System", ["top", "net", "disks"]),
        ("Setup", ["plugin", "sync", "paths", "doctor", "version"]),
        ("More", []),
    ];

    private static readonly (string Action, string Label)[] HintActions =
    [
        (EditorActionNames.CommandPalette, "Command palette"),
        (EditorActionNames.HistorySearch, "History search"),
        (EditorActionNames.FilePickerInsert, "Insert a path"),
        (EditorActionNames.FilePickerCd, "Change directory"),
        (EditorActionNames.OpenWizard, "Command wizard"),
    ];

    private static List<(string Chord, string Label)> KeyHints(PickleRuntime runtime)
    {
        var registry = runtime.KeyBindingRegistry;
        var hints = new List<(string Chord, string Label, int Order)>();
        foreach (var (chord, action) in registry.Bindings)
        {
            var order = Array.FindIndex(HintActions, h => h.Action == action);
            if (order < 0 && !action.StartsWith("panel.", StringComparison.Ordinal))
            {
                continue;
            }

            var label = order >= 0 ? HintActions[order].Label : registry.GetAction(action)?.Description ?? action;
            label = label.StartsWith("Open ", StringComparison.Ordinal) ? label[5..] : label;
            hints.Add((chord, label, order < 0 ? HintActions.Length : order));
        }

        // One chord per action (F1 over Ctrl+P): the shortest, then alphabetical.
        return hints
            .GroupBy(h => h.Label, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(h => h.Chord.Length).ThenBy(h => h.Chord, StringComparer.Ordinal).First())
            .OrderBy(h => h.Order)
            .ThenBy(h => h.Label, StringComparer.OrdinalIgnoreCase)
            .Select(h => (h.Chord, h.Label))
            .ToList();
    }

    private static List<string> WrapWords(string text, int width)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && TextWidth.VisibleWidth(line.ToString()) + 1 + TextWidth.VisibleWidth(word) > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }

            line.Append(line.Length > 0 ? " " : string.Empty).Append(word);
        }

        lines.Add(line.ToString());
        return lines;
    }
}
