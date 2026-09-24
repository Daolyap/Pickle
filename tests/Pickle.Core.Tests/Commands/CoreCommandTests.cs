using Pickle.Core.Commands;
using Pickle.Testing;

namespace Pickle.Core.Tests.Commands;

public class CoreCommandTests
{
    [Fact]
    public async Task DoctorReportsChecks()
    {
        using var t = TestPickle.Create(start: true);
        var result = await t.Runtime.Shell.InvokeAsync("pk doctor");
        var checks = result.Output.Select(o => o.BaseObject).OfType<DoctorCheck>().ToList();
        Assert.Contains(checks, c => c.Check == "Pickle" && c.Status == DoctorCheck.Ok);
        Assert.Contains(checks, c => c.Check == "Config" && c.Status == DoctorCheck.Ok);
        Assert.Contains(checks, c => c.Check == "git");
        Assert.Contains(checks, c => c.Check == "Sync" && c.Detail.Contains("pk sync init", StringComparison.Ordinal));
        Assert.Contains(checks, c => c.Check == "Terminal" && c.Detail.Contains("VT/ANSI supported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DoctorFlagsConfigProblems()
    {
        using var t = TestPickle.Create(start: true);
        File.WriteAllText(t.Paths.ConfigFile, """{ "editor": { "bellStyle": "loud" } }""");
        t.Run("pk reload");
        var result = await t.Runtime.Shell.InvokeAsync("pk doctor");
        Assert.Contains(result.Output.Select(o => o.BaseObject).OfType<DoctorCheck>(), c => c.Check == "Config" && c.Status == DoctorCheck.Error && c.Detail.Contains("bellStyle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PathsListsEveryLocation()
    {
        using var t = TestPickle.Create(start: true);
        var entries = (await t.Runtime.Shell.InvokeAsync("pk paths")).Output.Select(o => o.BaseObject).OfType<PathEntry>().ToList();
        Assert.Contains(entries, e => e.Name == "Config" && e.Path == t.Paths.ConfigDir && e.Exists);
        Assert.Contains(entries, e => e.Name == "Plugins" && e.Path == t.Paths.PluginsDir);
        Assert.Contains(entries, e => e.Name == "SyncState");
    }

    [Fact]
    public void ReloadPicksUpExternalConfigEdits()
    {
        using var t = TestPickle.Create(start: true);
        File.WriteAllText(t.Paths.ConfigFile, """{ "prompt": { "gitTimeoutMs": 55 } }""");
        t.Run("pk reload");
        Assert.Equal(55, t.Runtime.Config.Current.Prompt.GitTimeoutMs);
        Assert.Contains("config\n", Pickle.Abstractions.TextWidth.StripAnsi(t.Terminal.RawOutput), StringComparison.Ordinal);
    }

    [Fact]
    public void EditorResolutionPrefersEnvironmentThenKnownEditors()
    {
        static string? OnPath(string name) => name is "code" or "nano" or "vim" ? "/usr/bin/" + name : null;

        var fromEnv = EditorLauncher.Resolve(v => v == "EDITOR" ? "vim -u NONE" : null, OnPath, isWindows: false)!;
        Assert.Equal("/usr/bin/vim", fromEnv.FileName);
        Assert.Equal(["-u", "NONE"], fromEnv.Arguments);
        Assert.True(fromEnv.Wait);

        var code = EditorLauncher.Resolve(_ => null, OnPath, isWindows: false)!;
        Assert.Equal("/usr/bin/code", code.FileName);
        Assert.False(code.Wait);

        var codeWait = EditorLauncher.Resolve(v => v == "VISUAL" ? "code --wait" : null, OnPath, isWindows: false)!;
        Assert.True(codeWait.Wait);

        Assert.Equal("/usr/bin/nano", EditorLauncher.Resolve(_ => null, n => n == "nano" ? "/usr/bin/nano" : null, isWindows: false)!.FileName);
        Assert.Null(EditorLauncher.Resolve(_ => null, _ => null, isWindows: true));
    }
}
