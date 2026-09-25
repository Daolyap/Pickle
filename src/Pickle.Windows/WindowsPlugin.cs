using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Windows.Commands;
using Pickle.Windows.Elevation;
using Pickle.Windows.TaskScheduler;
using Pickle.Windows.WindowsUpdate;
using Pickle.Windows.Winget;

namespace Pickle.Windows;

/// <summary>
/// Registers the Windows services (winget, Windows Update, Task Scheduler, elevation broker — "unsupported"
/// implementations on other OSes) and the <c>pk winget|upgrade|update|schedule</c> commands.
/// Services already present (e.g. test fakes) are left alone.
/// </summary>
public sealed class WindowsPlugin : IPicklePlugin
{
    public string Id => "pickle.windows";

    public string DisplayName => "Windows integrations";

    public string Description => "winget, Windows Update, Task Scheduler, elevation broker, Windows Terminal profile.";

    public void Initialize(IPickleContext context)
    {
        var services = context.Services;
        AddIfMissing<IElevationBroker>(services, () => new ElevationBroker(context.Log));
        AddIfMissing<IWingetService>(services, () => new WingetService(context));
        AddIfMissing(services, () => CreateWindowsUpdateService(context));
        AddIfMissing(services, () => CreateTaskSchedulerService(context));
        AddIfMissing(services, () => CreateToolInstaller(context));
        AddIfMissing<ISandboxService>(services, () => new Sandbox.WindowsSandboxService(context));

        context.Commands.Register(new WingetCommand());
        context.Commands.Register(new UpgradeCommand());
        context.Commands.Register(new UpdateCommand());
        context.Commands.Register(new ScheduleCommand());
        context.Commands.Register(new ToolCommand());
        context.Commands.Register(new SandboxCommand());

        Terminal.WindowsTerminalIntegration.Register(context);
    }

    private static IWindowsUpdateService CreateWindowsUpdateService(IPickleContext context)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsUpdateService(context);
        }

        return new UnsupportedWindowsUpdateService();
    }

    private static IToolInstaller CreateToolInstaller(IPickleContext context)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new UnsupportedToolInstaller();
        }

        var temporaryRoot = Path.Combine(Path.GetTempPath(), "pickle-tools", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new Tools.WingetToolInstaller(
            () => context.Services.Get<IWingetService>(),
            command => context.Wizards.FindForCommand(command)?.WingetId,
            new Tools.RegistryPathStore(),
            temporaryRoot);
    }

    private static ITaskSchedulerService CreateTaskSchedulerService(IPickleContext context)
    {
        if (OperatingSystem.IsWindows())
        {
            return new TaskSchedulerService(context);
        }

        return new UnsupportedTaskSchedulerService();
    }

    private static void AddIfMissing<T>(IPickleServices services, Func<T> create)
        where T : class
    {
        if (services.Get<T>() is null)
        {
            services.Add(create());
        }
    }
}
