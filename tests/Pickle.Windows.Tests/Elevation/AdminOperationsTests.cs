using Pickle.Abstractions.Services;
using Pickle.Windows.Elevation;

namespace Pickle.Windows.Tests.Elevation;

public class AdminOperationsTests
{
    [Theory]
    [InlineData("Spooler", "restart")]
    [InlineData("wuauserv", "automatic")]
    [InlineData("MyApp.Svc@1", "disabled")]
    public void ServiceControlAcceptsPlainNamesAndTheFixedActions(string name, string action)
    {
        var op = ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.ServiceControl, [name, action]));

        Assert.Equal([name, action], op.Ids);
    }

    [Theory]
    [InlineData("Spooler; calc", "start")]
    [InlineData("Spooler", "delete")]
    [InlineData("Spooler", "start /y")]
    [InlineData("-name", "start")]
    [InlineData(@"..\x", "start")]
    [InlineData("Print Spooler", "start")]
    public void ServiceControlRejectsAnythingElse(string name, string action) =>
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.ServiceControl, [name, action])));

    [Fact]
    public void HostsWriteOnlyTakesAWellFormedFile()
    {
        ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.HostsFileWrite, ["127.0.0.1 localhost\r\n# fine\r\n"]));

        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.HostsFileWrite, ["MZ\u0090\0\u0003"])));
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.HostsFileWrite, ["not a hosts line"])));
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.HostsFileWrite, [])));
    }

    [Fact]
    public void MachineEnvironmentTakesNameValueOrJustNameToRemove()
    {
        Assert.Equal(["EDITOR", "vim"], ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.MachineEnvironmentSet, ["EDITOR", "vim"])).Ids);
        Assert.Equal(["EDITOR"], ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.MachineEnvironmentSet, ["EDITOR"])).Ids);
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.MachineEnvironmentSet, ["ComSpec", @"C:\evil.exe"])));
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.MachineEnvironmentSet, ["PATHEXT", ".EXE"])));
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.MachineEnvironmentSet, ["A B", "x"])));
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.MachineEnvironmentSet, ["X", "a\nb"])));
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.MachineEnvironmentSet, ["X", "1", "2"])));
    }

    [Fact]
    public async Task ValidatedOperationsReachTheExecutor()
    {
        var executor = new FakeExecutor();
        var progress = new Progress<string>();

        await ElevatedOperations.ExecuteAsync(ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.ServiceControl, ["Spooler", "stop"])), executor, progress, CancellationToken.None);
        await ElevatedOperations.ExecuteAsync(ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.HostsFileWrite, ["::1 localhost\n"])), executor, progress, CancellationToken.None);
        await ElevatedOperations.ExecuteAsync(ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.MachineEnvironmentSet, ["EDITOR"])), executor, progress, CancellationToken.None);

        Assert.Equal(["service Spooler stop", "hosts 14", "env EDITOR=<removed>"], executor.Calls);
    }
}
