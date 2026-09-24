using Pickle.Core.Commands;

namespace Pickle.Core.Tests.Commands;

public sealed class ExecutableLocatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pickle-locator").FullName;
    private readonly string _name = OperatingSystem.IsWindows() ? "tool.exe" : "tool";

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Plant(string directory, bool executable = true)
    {
        var path = Path.Combine(directory, _name);
        File.WriteAllText(path, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows() && executable)
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    [Fact]
    public void FindsProgramsOnlyInFullyQualifiedPathEntries()
    {
        var planted = Plant(_dir);
        // PATHEXT supplies the extension on Windows (tool.EXE), and file names there are case-insensitive.
        Assert.Equal(planted, ExecutableLocator.Find("tool", _dir), ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal(planted, ExecutableLocator.Find(_name, "relative" + Path.PathSeparator + _dir), ignoreCase: OperatingSystem.IsWindows());
        Assert.Null(ExecutableLocator.Find("tool", "."));
        Assert.Null(ExecutableLocator.Find("tool", string.Empty));
    }

    [Fact]
    public void RelativePathsAreNeverResolvedAgainstTheCurrentDirectory()
    {
        var planted = Plant(_dir);
        Assert.Null(ExecutableLocator.Find("./" + _name, _dir));
        Assert.Null(ExecutableLocator.Find("sub" + Path.DirectorySeparatorChar + _name, _dir));
        Assert.Equal(planted, ExecutableLocator.Find(planted));
        Assert.Null(ExecutableLocator.Find(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void NonExecutableFilesAreSkippedOnUnix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no execute bit");
        Plant(_dir, executable: false);
        Assert.Null(ExecutableLocator.Find("tool", _dir));
    }

    [Fact]
    public async Task ProcessRunnerReportsProgramsThatAreNotOnPathAsNotFound()
    {
        var result = await ProcessRunner.RunAsync("pickle-no-such-program-" + Guid.NewGuid().ToString("N")[..8], [], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ProcessRunner.NotFound, result.ExitCode);
    }
}
