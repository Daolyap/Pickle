using Pickle.Core.Syntax;

namespace Pickle.Core.Tests.Syntax;

public sealed class CommandCacheTests
{
    [Fact]
    public void PathScanSurvivesDanglingSymlinks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows; the execute-bit check is Unix-only");
        var dir = Directory.CreateTempSubdirectory("pickle-path").FullName;
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "aaa-broken"), Path.Combine(dir, "missing-target"));
            var tools = Enumerable.Range(0, 10).Select(i => $"tool-{i}").ToList();
            foreach (var tool in tools)
            {
                var path = Path.Combine(dir, tool);
                File.WriteAllText(path, "#!/bin/sh\n");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var names = CommandCache.ScanPath(dir);
            Assert.All(tools, tool => Assert.Contains(tool, names));
            Assert.DoesNotContain("aaa-broken", names);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
