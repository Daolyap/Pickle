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

    public string Description => "winget, Windows Update, Task Scheduler, elevation broker, Windows Terminal profile, fonts.";

    public void Initialize(IPickleContext context)
    {
        var services = context.Services;
        AddIfMissing<IElevationBroker>(services, () => new ElevationBroker(context.Log));
        AddIfMissing<IWingetService>(services, () => new WingetService(context));
        AddIfMissing(services, () => CreateWindowsUpdateService(context));
        AddIfMissing(services, () => CreateTaskSchedulerService(context));
        AddIfMissing(services, () => CreateToolInstaller(context));
        AddIfMissing<ISandboxService>(services, () => new Sandbox.WindowsSandboxService(context));
        AddIfMissing<ITerminalSchemeSource>(services, Terminal.WindowsTerminalSchemes.ForCurrentUser);
        if (OperatingSystem.IsWindows())
        {
            AddIfMissing<IFontService>(services, Fonts.WindowsFontService.ForCurrentSystem);
            AddIfMissing<IDiskLayoutService>(services, () => new Storage.DiskLayoutService(() => context.Shell));
            RegisterAdministration(context);
        }

        context.Commands.Register(new WingetCommand());
        context.Commands.Register(new UpgradeCommand());
        context.Commands.Register(new UpdateCommand());
        context.Commands.Register(new ScheduleCommand());
        context.Commands.Register(new ToolCommand());
        context.Commands.Register(new SandboxCommand());
        context.Commands.Register(new FontCommand());

        Terminal.WindowsTerminalIntegration.Register(context);
        RegisterUpgradeCheck(context);
    }

    // Windows backends for the administration panels (services, hosts file, environment); Pickle.Admin covers Linux and macOS.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RegisterAdministration(IPickleContext context)
    {
        var services = context.Services;
        AddIfMissing<ISystemServiceManager>(services, () => new Services.WindowsServiceManager(() => context.Shell, services.Require<IElevationBroker>()));
        AddIfMissing<ILogSource>(services, () => new Logs.WindowsEventLogSource(() => context.Shell));
        AddIfMissing<IHostsService>(services, () => new Services.WindowsHostsService(services.Require<IElevationBroker>()));
        AddIfMissing<IEnvironmentStore>(services, () => new Services.WindowsEnvironmentStore(context.Config, services.Require<IElevationBroker>()));
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

    // winget takes seconds and real CPU to list upgrades, so only the first running Pickle asks, every few hours; the
    // winget panel and the startup notice show the cached list at once.
    private static void RegisterUpgradeCheck(IPickleContext context)
    {
        if (!context.Config.Current.Shell.CheckForUpdates
            || context.Services.Get<IBackgroundWork>() is not { } background
            || context.Services.Get<IWingetService>() is not { IsSupported: true } winget)
        {
            return;
        }

        background.Register(new BackgroundJob(WingetCache.UpgradesKey, async ct =>
        {
            var upgrades = await winget.ListUpgradesAsync(context.Config.Current.Winget.IncludeUnknownVersions, ct).ConfigureAwait(false);
            background.Write(WingetCache.UpgradesKey, upgrades.ToList());
        })
        {
            Interval = TimeSpan.FromHours(4),
            InitialDelay = TimeSpan.FromMinutes(1),
        });
    }
}
