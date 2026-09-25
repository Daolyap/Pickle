using System.Management.Automation.Language;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.Commands;

namespace Pickle.Core.Hosting;

/// <summary>
/// Before an interactive line runs: when it calls a program that isn't installed but a known package provides it
/// (<c>7z</c> → 7-Zip), ask whether to install it first — for this user, all users or just this session, and whether
/// to add it to PATH — instead of letting the command fail. Temporary installs are removed when the shell exits.
/// </summary>
internal sealed class MissingToolPrompt
{
    private readonly PickleRuntime _runtime;
    private readonly HashSet<string> _declined = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _installing;

    public MissingToolPrompt(PickleRuntime runtime) => _runtime = runtime;

    /// <summary>Ctrl+C while a tool installs cancels it (true when there was one to cancel).</summary>
    public bool CancelInstall()
    {
        var cts = Volatile.Read(ref _installing);
        if (cts is null)
        {
            return false;
        }

        cts.Cancel();
        return true;
    }

    /// <summary>False when the user cancelled: the line must not run.</summary>
    public bool BeforeExecute(string command)
    {
        if (!_runtime.Config.Current.Shell.AskToInstallMissingTools || !_runtime.Terminal.IsInteractive || _runtime.Options.Headless
            || _runtime.Services.Get<IToolInstaller>() is not { IsSupported: true } installer)
        {
            return true;
        }

        foreach (var name in CommandNames(command))
        {
            if (_declined.Contains(name) || installer.Find(name) is not { } package || IsAvailable(name))
            {
                continue;
            }

            switch (Ask(name, package, out var options))
            {
                case Answer.Cancel:
                    return false;
                case Answer.RunAnyway:
                    _declined.Add(name);
                    continue;
                default:
                    if (!Install(installer, package, options))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    /// <summary>Uninstalls this session's temporary tools (on exit).</summary>
    public void RemoveTemporary()
    {
        if (_runtime.Services.Get<IToolInstaller>() is not { IsSupported: true } installer || installer.TemporaryInstalls.Count == 0)
        {
            return;
        }

        var theme = _runtime.Themes.Current;
        var names = string.Join(", ", installer.TemporaryInstalls.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase));
        _runtime.Terminal.Write(Ansi.Colorize($"Removing temporary tools: {names}…", theme.Ui.Muted) + "\r\n");
        try
        {
            var result = installer.RemoveTemporaryAsync().WaitAsync(TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
            if (!result.Success)
            {
                _runtime.Terminal.Write(Ansi.Colorize(result.Message, theme.Ui.Warning) + "\r\n");
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            _runtime.Log.Warn("tools", "Removing temporary tools failed", ex);
        }
    }

    /// <summary>Names of the commands a line invokes (static names only; functions it defines are skipped).</summary>
    internal static IReadOnlyList<string> CommandNames(string line)
    {
        var ast = Parser.ParseInput(line, out _, out _);
        var defined = ast.FindAll(a => a is FunctionDefinitionAst, true).Cast<FunctionDefinitionAst>().Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. ast.FindAll(a => a is CommandAst, true)
            .Cast<CommandAst>()
            .Select(c => c.GetCommandName())
            .OfType<string>()
            .Where(n => n.Length > 0 && !defined.Contains(n) && !n.Contains('/', StringComparison.Ordinal) && !n.Contains('\\', StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private bool IsAvailable(string name)
    {
        if (ExecutableLocator.Find(name) is not null)
        {
            return true;
        }

        try
        {
            var result = _runtime.Shell.InvokeAsync(
                "param($n) @(Get-Command -Name $n -ErrorAction Ignore).Count -gt 0",
                new Dictionary<string, object?> { ["n"] = name }).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return result.Output.Count > 0 && result.Output[0].BaseObject is true;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            // Unsure: don't get in the way.
            return true;
        }
    }

    private enum Answer
    {
        Install,
        RunAnyway,
        Cancel,
    }

    private Answer Ask(string name, ToolPackage package, out ToolInstallOptions options)
    {
        var terminal = _runtime.Terminal;
        var theme = _runtime.Themes.Current;
        var addToPath = _runtime.Config.Current.Shell.AddInstalledToolsToPath;
        string Key(string key) => Ansi.Colorize(key, theme.Ui.Accent, bold: true);
        string PathLine() => "  " + Key("P") + " add to PATH: " + (addToPath ? Ansi.Colorize("yes", theme.Ui.Success) : Ansi.Colorize("no (this session only)", theme.Ui.Muted));

        terminal.Write(Ansi.Colorize("? ", theme.Ui.Accent, bold: true)
            + $"'{name}' isn't installed. It comes with {package.Name} " + Ansi.Colorize($"(winget {package.WingetId})", theme.Ui.Muted) + ". Install it first?\r\n");
        terminal.Write("  " + Key("Enter") + " just for me   " + Key("M") + " all users   " + Key("T") + " this session only   "
            + Key("N") + " run anyway   " + Key("Esc") + " cancel\r\n");
        terminal.Write(PathLine());
        terminal.Flush();

        terminal.SetEditMode(true);
        try
        {
            while (true)
            {
                var key = terminal.ReadKey();
                ToolInstallScope? scope = key.Key switch
                {
                    ConsoleKey.Enter or ConsoleKey.Y or ConsoleKey.U => ToolInstallScope.User,
                    ConsoleKey.M => ToolInstallScope.Machine,
                    ConsoleKey.T => ToolInstallScope.Temporary,
                    _ => null,
                };
                if (scope is { } chosen)
                {
                    options = new ToolInstallOptions(chosen, addToPath);
                    terminal.Write("\r\n");
                    return Answer.Install;
                }

                if (key.Key == ConsoleKey.P)
                {
                    addToPath = !addToPath;
                    terminal.Write("\r" + Ansi.ClearToEndOfLine + PathLine());
                    terminal.Flush();
                    continue;
                }

                if (key.Key == ConsoleKey.N)
                {
                    options = new ToolInstallOptions();
                    terminal.Write("\r\n");
                    return Answer.RunAnyway;
                }

                if (key.Key == ConsoleKey.Escape || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
                {
                    options = new ToolInstallOptions();
                    terminal.Write("\r\n" + Ansi.Colorize("  cancelled", theme.Ui.Muted) + "\r\n");
                    return Answer.Cancel;
                }
            }
        }
        finally
        {
            terminal.SetEditMode(false);
        }
    }

    private bool Install(IToolInstaller installer, ToolPackage package, ToolInstallOptions options)
    {
        var terminal = _runtime.Terminal;
        var theme = _runtime.Themes.Current;
        var status = new StatusLine(terminal, theme.Ui.Muted);
        using var cts = new CancellationTokenSource();
        Volatile.Write(ref _installing, cts);
        ToolInstallResult result;
        try
        {
            result = installer.InstallAsync(package, options, status, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            result = new ToolInstallResult(false, "Installation cancelled.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            result = new ToolInstallResult(false, ex.Message);
        }
        finally
        {
            Volatile.Write(ref _installing, null);
            status.Clear();
        }

        terminal.Write("  " + (result.Success ? Ansi.Colorize("✓ " + result.Message, theme.Ui.Success) : Ansi.Colorize("✖ " + result.Message, theme.Ui.Error)) + "\r\n");
        return result.Success;
    }

    /// <summary>One overwritten line of progress; reports can come from any thread.</summary>
    private sealed class StatusLine(Terminal.ITerminal terminal, string color) : IProgress<string>
    {
        private readonly object _gate = new();
        private bool _shown;

        public void Report(string value)
        {
            lock (_gate)
            {
                var width = Math.Max(10, terminal.Width - 3);
                var text = TextWidth.VisibleWidth(value) > width ? TextWidth.Truncate(value, width) : value;
                terminal.Write("\r" + Ansi.ClearToEndOfLine + "  " + Ansi.Colorize(text, color));
                terminal.Flush();
                _shown = true;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                if (_shown)
                {
                    terminal.Write("\r" + Ansi.ClearToEndOfLine);
                    _shown = false;
                }
            }
        }
    }
}
