using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Admin.Logs;
using Pickle.Admin.Packages;
using Pickle.Admin.Privilege;
using Pickle.Admin.Scheduling;
using Pickle.Admin.Services;

namespace Pickle.Admin;

/// <summary>
/// Registers the administration services for Linux and macOS (Windows registers its own in Pickle.Windows; services
/// already present, such as test fakes, are left alone) and their <c>pk</c> commands.
/// </summary>
public sealed class AdminPlugin : IPicklePlugin
{
    public string Id => "pickle.admin";

    public string DisplayName => "Administration";

    public string Description => "Services, logs, packages, hosts/PATH/environment and timers (Linux, macOS and Windows).";

    public void Initialize(IPickleContext context)
    {
        var services = context.Services;
        var runner = services.Require<IProgramRunner>();
        AddIfMissing<IPrivilegeService>(services, () => new UnixPrivilegeService(runner));
        AddIfMissing<ISystemServiceManager>(services, () => CreateServiceManager(runner, services.Require<IPrivilegeService>()));
        AddIfMissing<ILogSource>(services, () => CreateLogSource(runner));
        if (!OperatingSystem.IsWindows())
        {
            AddIfMissing<IJobScheduler>(services, () => new UnixJobScheduler(runner, services.Require<IPrivilegeService>()));
            AddIfMissing<ISystemPackageManager>(services, () => PackageManagers.Detect(runner, services.Require<IPrivilegeService>()));
        }

        AddIfMissing<IHostsService>(services, () => new Hosts.UnixHostsService(services.Require<IPrivilegeService>()));
        AddIfMissing<IEnvironmentStore>(services, () => new EnvVars.UnixEnvironmentStore(context.Config, services.Require<IPrivilegeService>()));
    }

    private static ISystemServiceManager CreateServiceManager(IProgramRunner runner, IPrivilegeService privilege) =>
        OperatingSystem.IsMacOS() ? new LaunchdServiceManager(runner, privilege) : new SystemdServiceManager(runner, privilege);

    private static ILogSource CreateLogSource(IProgramRunner runner)
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacLogSource(runner);
        }

        var journal = new JournaldLogSource(runner);
        return journal.IsSupported ? journal : new SyslogFileLogSource();
    }

    internal static void AddIfMissing<T>(IPickleServices services, Func<T> create)
        where T : class
    {
        if (services.Get<T>() is null)
        {
            services.Add(create());
        }
    }
}
