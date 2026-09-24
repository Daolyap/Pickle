using System.Diagnostics;
using Pickle.Core.Sync;
using Pickle.Testing;

namespace Pickle.Core.Tests.Sync;

public class SyncServiceTests
{
    private static string Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return output;
    }

    [Fact]
    public void FolderBackendMovesSettingsBetweenMachines()
    {
        var shared = Directory.CreateTempSubdirectory("pickle-sync-folder").FullName;
        try
        {
            using var a = TestPickle.Create(start: true);
            using var b = TestPickle.Create(start: true);

            a.Run("pk config set theme a-theme");
            a.Run("pk config set editor.bellStyle visual");
            a.Run("pk config set prompt.gitTimeoutMs 777 --local");
            a.Run($"pk sync init '{shared}'");
            Assert.Equal("folder", a.Runtime.Config.Current.Sync.Backend);
            Assert.Contains("\"backend\": \"folder\"", File.ReadAllText(a.Paths.LocalConfigFile), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(shared, "config.json")));
            Assert.DoesNotContain("777", File.ReadAllText(Path.Combine(shared, "config.json")), StringComparison.Ordinal);

            b.Run($"pk sync init '{shared}'");
            Assert.Equal("a-theme", b.Runtime.Config.Current.Theme);
            Assert.Equal("visual", b.Runtime.Config.Current.Editor.BellStyle);
            Assert.Equal(400, b.Runtime.Config.Current.Prompt.GitTimeoutMs);

            // Both machines change different settings; then each syncs.
            b.Run("pk config set editor.autosuggestions false");
            b.Run("pk config set history.maxEntries 123");
            b.Run("pk sync push");
            a.Run("pk sync pull");
            Assert.False(a.Runtime.Config.Current.Editor.Autosuggestions);
            Assert.Equal(123, a.Runtime.Config.Current.History.MaxEntries);

            var status = a.Run("pk sync status");
            Assert.Empty(status);
            Assert.Contains("up to date", a.Terminal.RawOutput, StringComparison.Ordinal);

            a.Run("pk sync off");
            Assert.Equal("none", a.Runtime.Config.Current.Sync.Backend);
            Assert.False(File.Exists(a.Paths.SyncStateFile));
        }
        finally
        {
            Directory.Delete(shared, recursive: true);
        }
    }

    [Fact]
    public void GitBackendPushesAndPullsThroughABareRepository()
    {
        var root = Directory.CreateTempSubdirectory("pickle-sync-git").FullName;
        try
        {
            var bare = Path.Combine(root, "settings.git");
            Git(root, "init", "--bare", "-q", bare);
            var url = new Uri(bare).AbsoluteUri;

            using var a = TestPickle.Create(start: true);
            using var b = TestPickle.Create(start: true);
            a.Run("pk config set theme from-git");
            File.WriteAllText(a.Paths.ProfileFile, "# profile from a\n");
            a.Run($"pk sync init '{url}'");
            Assert.Equal("git", a.Runtime.Config.Current.Sync.Backend);

            var log = Git(root, "--git-dir", bare, "log", "--all", "--format=%s");
            Assert.Contains("Pickle sync from", log, StringComparison.Ordinal);

            b.Run($"pk sync init '{url}' --backend git");
            Assert.Equal("from-git", b.Runtime.Config.Current.Theme);
            Assert.Equal("# profile from a\n", File.ReadAllText(b.Paths.ProfileFile));

            b.Run("pk config set editor.bellStyle audible");
            b.Run("pk sync push");
            a.Run("pk sync pull");
            Assert.Equal("audible", a.Runtime.Config.Current.Editor.BellStyle);
            Assert.True(Directory.Exists(Path.Combine(a.Paths.DataDir, "sync-repo", ".git")));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StatusWithoutSetupExplainsHowToStart()
    {
        using var t = TestPickle.Create(start: true);
        var report = await t.Runtime.Sync.StatusAsync();
        Assert.False(report.Success);
        Assert.Contains("pk sync init", report.Message, StringComparison.Ordinal);
        Assert.IsType<SyncService>(t.Runtime.Sync);
    }
}
