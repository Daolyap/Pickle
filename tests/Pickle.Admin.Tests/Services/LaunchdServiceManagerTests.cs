using Pickle.Abstractions.Services;
using Pickle.Admin.Services;

namespace Pickle.Admin.Tests.Services;

public class LaunchdServiceManagerTests
{
    [Fact]
    public void ParsesLaunchctlList()
    {
        const string output = "PID\tStatus\tLabel\n312\t0\tcom.apple.Finder\n-\t0\tcom.example.idle\n-\t78\tcom.example.broken\n-\t0\tbad label with spaces\n";

        var rows = LaunchdServiceManager.ParseList(output);

        Assert.Equal(["com.apple.Finder", "com.example.broken", "com.example.idle"], rows.Select(r => r.Id));
        Assert.Equal(ServiceState.Running, rows[0].State);
        Assert.Equal(ServiceState.Failed, rows[1].State);
        Assert.Equal(ServiceState.Stopped, rows[2].State);
    }

    [Fact]
    public void ParsesTheSystemDomain()
    {
        const string output = "system = {\n\ttype = system\n\tservices = {\n\t\t    101      -   com.apple.cron\n\t\t      -      0   com.example.helper\n\t}\n}\n";

        var rows = LaunchdServiceManager.ParsePrintSystem(output);

        Assert.Equal(["com.apple.cron", "com.example.helper"], rows.Select(r => r.Id));
        Assert.Equal(ServiceState.Running, rows[0].State);
    }
}
