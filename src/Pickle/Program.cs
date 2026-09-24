using Pickle;
using Pickle.Abstractions;
using Pickle.Core;
using Pickle.Core.Logging;

var options = PickleOptions.Parse(args);

if (options.Errors.Count > 0)
{
    foreach (var error in options.Errors)
    {
        Console.Error.WriteLine($"pickle: {error}");
    }

    Console.Error.WriteLine("Run 'pickle --help' for usage.");
    return 2;
}

if (options.ShowHelp)
{
    Console.WriteLine(PickleOptions.HelpText);
    return 0;
}

if (options.ShowVersion)
{
    Console.WriteLine($"Pickle {PickleRuntime.Version} (PowerShell {PickleRuntime.PowerShellVersion}, .NET {Environment.Version})");
    return 0;
}

if (options.ElevatedHelperPipe is not null && options.ElevatedHelperNonce is not null)
{
    var paths = PicklePaths.Resolve();
    using var log = new FileLogger(paths.LogDir, FileLogger.ResolveLevel(options.LogLevel));
    return Pickle.Windows.Elevation.ElevatedHelper.Run(options.ElevatedHelperPipe, options.ElevatedHelperNonce, log);
}

if (options.InstallTerminalProfile)
{
    return Pickle.Windows.Terminal.WindowsTerminalProfile.Install(Environment.ProcessPath ?? "pickle.exe");
}

if (options.UninstallTerminalProfile)
{
    return Pickle.Windows.Terminal.WindowsTerminalProfile.Uninstall();
}

if (options.WriteTerminalFragment is not null)
{
    return Pickle.Windows.Terminal.WindowsTerminalProfile.WriteFragment(
        options.WriteTerminalFragment,
        options.FragmentCommandLine ?? Environment.ProcessPath ?? "pickle.exe",
        Console.Error);
}

return PickleApp.Run(options, BuiltInPlugins.Create());
