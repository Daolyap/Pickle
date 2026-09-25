using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.Hosting;
using Pickle.Testing;
using Pickle.Testing.Fakes;

namespace Pickle.Core.Tests.Hosting;

public sealed class MissingToolPromptTests
{
    [Fact]
    public void FindsTheCommandsALineCallsButNotFunctionsItDefines()
    {
        var names = MissingToolPrompt.CommandNames("7z a x.7z f | Out-Null; function helper { nmap -sn $args }; helper; & 'ffmpeg' -i a; .\\local.exe");
        Assert.Equal(["7z", "Out-Null", "nmap", "ffmpeg"], names);
    }

    [Fact]
    public void InstallsTheMissingToolForTheSessionThenRunsTheLine()
    {
        using var t = Start(out var installer);
        t.Terminal.Press("T");

        var result = t.Runtime.Repl.ExecuteLine("pickletool --version", echo: false);

        Assert.True(result.Success);
        var (package, options) = Assert.Single(installer.Installs);
        Assert.Equal("Pickle.TestTool", package.WingetId);
        Assert.Equal(new ToolInstallOptions(ToolInstallScope.Temporary, AddToPath: true), options);
        var screen = t.Terminal.GetScreenText();
        Assert.Contains("'pickletool' isn't installed", screen, StringComparison.Ordinal);
        Assert.Contains("✓ Installed Pickle test tool.", screen, StringComparison.Ordinal);
        Assert.Contains("ran pickletool --version", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void PTogglesAddToPathAndEnterInstallsForTheUser()
    {
        using var t = Start(out var installer);
        t.Terminal.Press("P", "Enter");

        t.Runtime.Repl.ExecuteLine("pickletool", echo: false);

        Assert.Equal(new ToolInstallOptions(ToolInstallScope.User, AddToPath: false), Assert.Single(installer.Installs).Options);
        Assert.Contains("add to PATH: no", t.Terminal.GetScreenText(), StringComparison.Ordinal);
    }

    [Fact]
    public void EscapeCancelsTheLineAndRecordsIt()
    {
        using var t = Start(out var installer);
        t.Terminal.Press("Escape");

        var result = t.Runtime.Repl.ExecuteLine("pickletool --version", echo: false);

        Assert.False(result.Success);
        Assert.True(result.Interrupted);
        Assert.Empty(installer.Installs);
        Assert.DoesNotContain("ran pickletool", t.Terminal.GetScreenText(), StringComparison.Ordinal);
        Assert.Equal("pickletool --version", t.Runtime.History.Entries[^1].CommandLine);
    }

    [Fact]
    public void RunAnywayIsRememberedForTheSession()
    {
        using var t = Start(out var installer, defineOnInstall: false);
        t.Terminal.Press("N");

        t.Runtime.Repl.ExecuteLine("pickletool", echo: false);
        t.Terminal.ClearRawOutput();
        t.Runtime.Repl.ExecuteLine("pickletool", echo: false);

        Assert.Empty(installer.Installs);
        Assert.DoesNotContain("isn't installed. It comes with", t.Terminal.RawOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedInstallDoesNotRunTheLine()
    {
        using var t = Start(out var installer);
        installer.Result = _ => new ToolInstallResult(false, "No package found matching the id.");
        t.Terminal.Press("Enter");

        var result = t.Runtime.Repl.ExecuteLine("pickletool", echo: false);

        Assert.False(result.Success);
        Assert.Contains("✖ No package found matching the id.", t.Terminal.GetScreenText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AvailableCommandsAndDisabledSettingNeverAsk()
    {
        using (var t = Start(out var installer))
        {
            t.Run("function global:pickletool { 'already here' }");
            t.Runtime.Repl.ExecuteLine("pickletool", echo: false);
            Assert.Empty(installer.Installs);
            Assert.Contains("already here", t.Terminal.GetScreenText(), StringComparison.Ordinal);
        }

        using (var t = Start(out var installer, configure: c => c.Shell.AskToInstallMissingTools = false))
        {
            t.Runtime.Repl.ExecuteLine("pickletool", echo: false);
            Assert.Empty(installer.Installs);
        }
    }

    private static TestPickle Start(out FakeToolInstaller installer, bool defineOnInstall = true, Action<PickleConfig>? configure = null)
    {
        var t = TestPickle.Create(width: 120, height: 40, start: true, configure: configure);
        var fake = new FakeToolInstaller();
        if (defineOnInstall)
        {
            fake.OnInstalled = _ => t.Run("function global:pickletool { \"ran pickletool $args\" }");
        }

        t.Runtime.ServiceRegistry.Add<IToolInstaller>(fake);
        installer = fake;
        return t;
    }
}
