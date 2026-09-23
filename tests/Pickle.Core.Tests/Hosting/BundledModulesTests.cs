using System.Diagnostics;

namespace Pickle.Core.Tests.Hosting;

/// <summary>
/// Runs the built pickle executable (src/Pickle/bin/&lt;config&gt;/net10.0) to check the modules bundled by
/// BundledModules.targets load. Skips if the exe hasn't been built (e.g. filtered test runs).
/// </summary>
public class BundledModulesTests
{
    [Fact]
    public void ThreadJobAndArchiveModulesAreAvailable()
    {
        var output = RunPickle("(Start-ThreadJob { 6 * 7 } | Wait-Job | Receive-Job); (Get-Command Compress-Archive).Source; (Get-Command Install-PSResource).Source");
        Assert.Equal(["42", "Microsoft.PowerShell.Archive", "Microsoft.PowerShell.PSResourceGet"], output);
    }

    [Fact]
    public void ProgressIsNotWrittenToRedirectedOutput()
    {
        var output = RunPickle("1..3 | ForEach-Object { Write-Progress -Activity work -PercentComplete ($_ * 30) }; 'done'");
        Assert.Equal(["done"], output);
    }

    private static string[] RunPickle(string command)
    {
        var exe = FindPickle();
        if (exe is null)
        {
            Assert.Skip("pickle executable not built");
        }

        var home = Directory.CreateTempSubdirectory("pickle-bundled").FullName;
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--no-logo");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        psi.Environment["PICKLE_HOME"] = home;
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "pickle timed out");
        Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}: {stderr.Result}");
        return stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? FindPickle()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pickle.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return null;
        }

        var name = OperatingSystem.IsWindows() ? "pickle.exe" : "pickle";
        foreach (var config in new[] { "Debug", "Release" })
        {
            var candidate = Path.Combine(dir.FullName, "src", "Pickle", "bin", config, "net10.0", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
