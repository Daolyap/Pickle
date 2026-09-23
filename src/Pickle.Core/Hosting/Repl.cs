using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Pickle.Abstractions;

namespace Pickle.Core.Hosting;

/// <summary>
/// The read-eval-print loop: prompt → line editor → translation → history → execute → hooks.
/// Also runs nested prompts ($Host.EnterNestedPrompt) and debugger stops.
/// </summary>
public sealed class Repl
{
    private readonly PickleRuntime _runtime;
    private int _nestedDepth;
    private int _exitNestedRequested;

    public Repl(PickleRuntime runtime) => _runtime = runtime;

    public int Run(CancellationToken cancellationToken = default)
    {
        Console.CancelKeyPress += OnCancelKeyPress;
        try
        {
            while (!_runtime.ExitRequested && !cancellationToken.IsCancellationRequested)
            {
                if (_runtime.Engine.SubmittedCommands.TryDequeue(out var submitted))
                {
                    ExecuteLine(submitted, echo: true);
                    continue;
                }

                var promptContext = _runtime.CreatePromptContext();
                RaiseHook(new HookEvent(HookKind.Prompt, Cwd: promptContext.Cwd));
                var prompt = _runtime.Prompt.Render(promptContext);

                string? line;
                _runtime.Terminal.SetEditMode(true);
                try
                {
                    line = _runtime.LineEditor.ReadLine(prompt, promptContext, cancellationToken);
                }
                finally
                {
                    _runtime.Terminal.SetEditMode(false);
                }

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ExecuteLine(line, echo: false);
            }
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            RaiseHook(new HookEvent(HookKind.Exit, Cwd: _runtime.Engine.CurrentDirectory));
        }

        return _runtime.ExitCode;
    }

    /// <summary>Translate, record, execute and fire hooks for one accepted line.</summary>
    public ExecutionResult ExecuteLine(string line, bool echo)
    {
        var cwdBefore = _runtime.Engine.CurrentDirectory;
        if (echo)
        {
            var ctx = _runtime.CreatePromptContext();
            _runtime.Terminal.Write(_runtime.Prompt.RenderTransient(ctx) + line + "\n");
        }

        var outcome = _runtime.Translation.Translate(line, cwdBefore);
        if (outcome.Changed && _runtime.Config.Current.Editor.ShowTranslatedCommand)
        {
            var color = _runtime.Themes.Current.Syntax.Translated;
            _runtime.Terminal.Write(Ansi.Colorize("→ " + outcome.Command, color) + "\n");
        }

        _runtime.History.Add(new HistoryEntry(
            line,
            DateTimeOffset.Now,
            cwdBefore,
            SessionId: _runtime.History.SessionId,
            Machine: Environment.MachineName));

        RaiseHook(new HookEvent(HookKind.PreExecute, line, cwdBefore));
        var result = _runtime.Engine.ExecuteInteractive(outcome.Command);
        _runtime.History.CompleteLast(result.Success, (long)result.Duration.TotalMilliseconds);
        RaiseHook(new HookEvent(HookKind.PostExecute, line, _runtime.Engine.CurrentDirectory, cwdBefore, result.Success, result.Duration));

        if (!string.Equals(cwdBefore, _runtime.Engine.CurrentDirectory, StringComparison.Ordinal))
        {
            RaiseHook(new HookEvent(HookKind.DirectoryChanged, line, _runtime.Engine.CurrentDirectory, cwdBefore));
        }

        return result;
    }

    // ───────────── Nested prompts ─────────────

    public void RunNestedPrompt()
    {
        _nestedDepth++;
        var myDepth = _nestedDepth;
        try
        {
            while (!_runtime.ExitRequested)
            {
                if (Interlocked.CompareExchange(ref _exitNestedRequested, 0, 1) == 1)
                {
                    break;
                }

                var prefix = new string('>', myDepth + 1);
                var line = ReadNestedLine($"{prefix} ");
                if (line is null || line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                RunNestedCommand(line);
            }
        }
        finally
        {
            _nestedDepth--;
        }
    }

    public void ExitNestedPrompt() => Interlocked.Exchange(ref _exitNestedRequested, 1);

    // ───────────── Debugger ─────────────

    public void OnDebuggerStop(DebuggerStopEventArgs e)
    {
        var ui = _runtime.Engine.Host.UI;
        var theme = _runtime.Themes.Current;
        var position = e.InvocationInfo?.PositionMessage;
        ui.WriteLine(Ansi.Colorize(e.Breakpoints.Count > 0 ? $"Hit {e.Breakpoints[0]}" : "Entering debug mode. Use h or ? for help.", theme.Ui.Warning));
        if (!string.IsNullOrEmpty(position))
        {
            ui.WriteLine(position);
        }

        var debugger = _runtime.Engine.MainRunspace.Debugger;
        while (true)
        {
            var line = ReadNestedLine("[DBG]: > ");
            if (line is null)
            {
                e.ResumeAction = DebuggerResumeAction.Stop;
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                line = "stepInto";
            }

            var output = new PSDataCollection<PSObject>();
            output.DataAdded += (_, args) =>
            {
                var item = output[args.Index];
                ui.WriteLine(item?.ToString() ?? string.Empty);
            };

            try
            {
                var command = new PSCommand();
                command.AddScript(line).AddCommand("Out-String").AddParameter("Stream", true);
                var result = debugger.ProcessCommand(command, output);
                if (result?.ResumeAction is { } resume)
                {
                    e.ResumeAction = resume;
                    return;
                }
            }
            catch (Exception ex) when (ex is RuntimeException or InvalidOperationException)
            {
                ui.WriteErrorLine(ex.Message);
            }
        }
    }

    private string? ReadNestedLine(string promptText)
    {
        var theme = _runtime.Themes.Current;
        var render = new Contracts.PromptRender(Ansi.Colorize(promptText, theme.Ui.Warning), null, "  ");
        _runtime.Terminal.SetEditMode(true);
        try
        {
            return _runtime.LineEditor.ReadLine(render, _runtime.CreatePromptContext());
        }
        finally
        {
            _runtime.Terminal.SetEditMode(false);
        }
    }

    private void RunNestedCommand(string line)
    {
        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
        ps.AddScript(line);
        ps.Commands.Commands[0].MergeMyResults(PipelineResultTypes.Error, PipelineResultTypes.Output);
        ps.AddCommand("Out-Default");
        try
        {
            ps.Invoke();
        }
        catch (RuntimeException ex)
        {
            _runtime.Engine.Host.UI.WriteErrorLine(ex.ErrorRecord.ToString());
        }
    }

    // ───────────── Helpers ─────────────

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        if (_runtime.Engine.StopCurrent())
        {
            e.Cancel = true;
        }
        else
        {
            // Not running a pipeline: never let Ctrl+C kill the shell.
            e.Cancel = true;
        }
    }

    private void RaiseHook(HookEvent hookEvent)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            _runtime.Hooks.RaiseAsync(hookEvent).AsTask().GetAwaiter().GetResult();
            if (sw.ElapsedMilliseconds > 200)
            {
                _runtime.Log.Warn("hooks", $"{hookEvent.Kind} hooks took {sw.ElapsedMilliseconds} ms");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _runtime.Log.Warn("hooks", $"{hookEvent.Kind} failed", ex);
        }
    }
}
