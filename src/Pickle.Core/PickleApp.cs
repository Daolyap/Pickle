using System.Text;
using Pickle.Abstractions;
using Pickle.Core.Hosting;
using Pickle.Core.Logging;
using Pickle.Core.Terminal;

namespace Pickle.Core;

/// <summary>Runs Pickle in the mode selected by <see cref="PickleOptions"/>. Called by pickle.exe's Program.</summary>
public static class PickleApp
{
    public static int Run(PickleOptions options, IReadOnlyList<IPicklePlugin> builtInPlugins, ITerminal? terminal = null, PicklePaths? paths = null)
    {
        paths ??= PicklePaths.Resolve();
        paths.EnsureCreated();
        var log = new FileLogger(paths.LogDir, FileLogger.ResolveLevel(options.LogLevel));
        terminal ??= new ConsoleTerminal();

        using var runtime = new PickleRuntime(options, terminal, paths, log);
        log.Info("startup", $"Pickle {PickleRuntime.Version} (PowerShell {PickleRuntime.PowerShellVersion}) starting; args mode: {Mode(options)}");
        runtime.InitializeComponents();
        runtime.Start(builtInPlugins);

        if (options.Command is not null)
        {
            return ToExitCode(runtime, runtime.Engine.ExecuteInteractive(options.Command));
        }

        if (options.File is not null)
        {
            var script = new StringBuilder("& ").Append(Quote(options.File));
            foreach (var arg in options.FileArguments)
            {
                script.Append(' ').Append(Quote(arg));
            }

            return ToExitCode(runtime, runtime.Engine.ExecuteInteractive(script.ToString()));
        }

        if (options.Headless || !terminal.IsInteractive)
        {
            return RunHeadless(runtime);
        }

        if (!options.NoLogo && runtime.Config.Current.Shell.ShowStartupBanner)
        {
            StartupBanner.Write(runtime);
        }

        runtime.FirstRun.Run(runtime);
        return runtime.Repl.Run();
    }

    private static int RunHeadless(PickleRuntime runtime)
    {
        ExecutionResult? last = null;
        while (!runtime.ExitRequested && Console.In.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                last = runtime.Repl.ExecuteLine(line, echo: false);
            }
        }

        return last is null ? runtime.ExitCode : ToExitCode(runtime, last);
    }

    private static int ToExitCode(PickleRuntime runtime, ExecutionResult result)
    {
        if (runtime.ExitRequested)
        {
            return runtime.ExitCode;
        }

        if (result.ExitCode is { } code && code != 0)
        {
            return code;
        }

        return result.Success ? 0 : 1;
    }

    private static string Mode(PickleOptions o) =>
        o.Command is not null ? "command" : o.File is not null ? "file" : o.Headless ? "headless" : "interactive";

    private static string Quote(string s) => Translation.PowerShellText.SingleQuote(s);
}
