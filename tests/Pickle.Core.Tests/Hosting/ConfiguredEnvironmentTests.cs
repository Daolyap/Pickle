using Pickle.Abstractions;
using Pickle.Core.Hosting;
using Pickle.Core.Logging;

namespace Pickle.Core.Tests.Hosting;

[Collection(ProcessWideStateCollection.Name)]
public class ConfiguredEnvironmentTests : IDisposable
{
    private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
    private readonly string _logDir = Directory.CreateTempSubdirectory("pickle-env-log").FullName;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _path);
        Environment.SetEnvironmentVariable("PICKLE_TEST_A", null);
        Environment.SetEnvironmentVariable("PICKLE_TEST_B", null);
    }

    private IPickleLogger Log => new FileLogger(_logDir, PickleLogLevel.Debug);

    [Fact]
    public void SetsVariablesAndExpandsReferences()
    {
        Environment.SetEnvironmentVariable("PICKLE_TEST_A", "alpha");
        var shell = new ShellSettings();
        shell.Environment["PICKLE_TEST_B"] = "%PICKLE_TEST_A%-beta";

        ConfiguredEnvironment.Apply(shell, Log);

        Assert.Equal("alpha-beta", Environment.GetEnvironmentVariable("PICKLE_TEST_B"));
    }

    [Fact]
    public void ProtectedAndInvalidNamesAreSkipped()
    {
        var shell = new ShellSettings();
        shell.Environment["LD_PRELOAD"] = "/tmp/evil.so";
        shell.Environment["not valid"] = "x";
        var before = Environment.GetEnvironmentVariable("LD_PRELOAD");

        ConfiguredEnvironment.Apply(shell, Log);

        Assert.Equal(before, Environment.GetEnvironmentVariable("LD_PRELOAD"));
    }

    [Fact]
    public void PathPrependGoesFirstOnceAndSkipsFoldersAlreadyThere()
    {
        var dir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "pickle-bin";
        var shell = new ShellSettings();
        shell.PathPrepend.AddRange([dir, dir, string.Empty]);

        ConfiguredEnvironment.Apply(shell, Log);
        ConfiguredEnvironment.Apply(shell, Log);

        var parts = Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator);
        Assert.Equal(dir, parts[0]);
        Assert.Equal(1, parts.Count(p => p == dir));
    }
}
