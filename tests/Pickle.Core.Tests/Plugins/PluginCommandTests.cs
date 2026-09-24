using Pickle.Core.Plugins;
using Pickle.Testing;

namespace Pickle.Core.Tests.Plugins;

public class PluginCommandTests
{
    private static readonly string PsFixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "SamplePsPlugin");

    [Fact]
    public void InstallFromPathLoadsTheModuleAndRemoveDeletesIt()
    {
        using var t = TestPickle.Create(start: true);
        var source = Path.Combine(t.Home, "src", "SamplePsPlugin");
        PluginLoadingTests.CopyDirectory(PsFixture, source);

        t.Run($"pk plugin install '{source}'");
        Assert.True(File.Exists(Path.Combine(t.Paths.PluginsDir, "SamplePsPlugin", "SamplePsPlugin.psd1")));
        Assert.Equal(["Hello, Ann!"], t.Run("pk sample-hello Ann"));
        Assert.Contains(InstalledPlugins.Read(t.Paths), p => p.Name == "SamplePsPlugin" && p.Source == "path");

        var list = t.Run("pk plugin list | Where-Object Id -eq SamplePsPlugin | ForEach-Object Status");
        Assert.Equal(["Loaded"], list);

        t.Run("pk plugin disable SamplePsPlugin");
        Assert.Contains("SamplePsPlugin", t.Runtime.Config.Current.Plugins.Disabled);
        t.Run("pk plugin enable SamplePsPlugin");
        Assert.DoesNotContain("SamplePsPlugin", t.Runtime.Config.Current.Plugins.Disabled);

        t.Run("pk plugin remove SamplePsPlugin");
        Assert.False(Directory.Exists(Path.Combine(t.Paths.PluginsDir, "SamplePsPlugin")));
        Assert.Empty(InstalledPlugins.Read(t.Paths));
    }

    [Fact]
    public void NewScaffoldsAPowerShellPluginThatInstallsAndRuns()
    {
        using var t = TestPickle.Create(start: true);
        var work = Directory.CreateDirectory(Path.Combine(t.Home, "work")).FullName;
        t.Run($"Set-Location -LiteralPath '{work}'; pk plugin new Weather");
        Assert.True(File.Exists(Path.Combine(work, "Weather", "Weather.psd1")));
        Assert.Equal(["True"], t.Run($"(Import-PowerShellDataFile -LiteralPath '{Path.Combine(work, "Weather", "Weather.psd1")}').PrivateData.ContainsKey('Pickle')"));

        t.Run("pk plugin install ./Weather");
        Assert.Equal(["Hello, Zoe, from Weather!"], t.Run("pk weather Zoe"));
        Assert.NotNull(t.Runtime.PromptSegmentRegistry.Get("weather"));
    }

    [Fact]
    public void NewDotnetScaffoldsAProjectWithManifest()
    {
        using var t = TestPickle.Create(start: true);
        var work = Directory.CreateDirectory(Path.Combine(t.Home, "work")).FullName;
        t.Run($"Set-Location -LiteralPath '{work}'; pk plugin new My.Tool --dotnet");
        var dir = Path.Combine(work, "My.Tool");
        Assert.Contains("Pickle.Abstractions", File.ReadAllText(Path.Combine(dir, "My.Tool.csproj")), StringComparison.Ordinal);
        Assert.Contains("\"assembly\": \"My.Tool.dll\"", File.ReadAllText(Path.Combine(dir, "plugin.json")), StringComparison.Ordinal);
        Assert.Contains("IPicklePlugin", File.ReadAllText(Path.Combine(dir, "MyToolPlugin.cs")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://github.com/me/pickle-weather.git", true, "pickle-weather")]
    [InlineData("git@github.com:me/pickle-weather.git", true, "pickle-weather")]
    [InlineData("file:///tmp/repos/thing", true, "thing")]
    [InlineData(@"file://C:\Users\me\repos\thing\", true, "thing")]
    [InlineData("PSWeather", false, "PSWeather")]
    public void GitUrlsAreRecognized(string text, bool isGit, string name)
    {
        Assert.Equal(isGit, PluginCommand.IsGitUrl(text));
        Assert.Equal(name, PluginCommand.RepositoryName(text));
    }

    [Fact]
    public void InstallFromGitClonesIntoThePluginsFolder()
    {
        using var t = TestPickle.Create(start: true);
        var repo = Path.Combine(t.Home, "repos", "SamplePsPlugin");
        PluginLoadingTests.CopyDirectory(PsFixture, repo);
        foreach (var args in new[] { "init -q", "add -A", "-c user.name=t -c user.email=t@t commit -q -m init" })
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args.Split(' '))
            {
                psi.ArgumentList.Add(a);
            }

            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }

        t.Run($"pk plugin install '{new Uri(repo).AbsoluteUri}'");
        Assert.True(File.Exists(Path.Combine(t.Paths.PluginsDir, "SamplePsPlugin", "SamplePsPlugin.psm1")));
        Assert.Equal(["Hello, git!"], t.Run("pk sample-hello git"));
    }
}
