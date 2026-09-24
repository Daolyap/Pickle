using Pickle.Abstractions;
using Pickle.Core.Translation;
using Pickle.Testing;

namespace Pickle.Core.Tests.Translation;

public class TranslationPipelineTests
{
    [Fact]
    public void TypedLinesAreTranslatedAndShown()
    {
        using var t = TestPickle.Create(start: true);
        var name = "PICKLE_T_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        t.Runtime.Repl.ExecuteLine($"export {name}=bar", echo: false);
        Assert.Equal("bar", Environment.GetEnvironmentVariable(name));
        Assert.Contains($"→ $env:{name} = 'bar'", t.Terminal.GetScreenText(), StringComparison.Ordinal);

        t.Runtime.Repl.ExecuteLine($"unset {name}", echo: false);
        Assert.Null(Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void EnvPrefixSetsVariableOnlyForTheCommand()
    {
        using var t = TestPickle.Create(start: true);
        var name = "PICKLE_P_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        t.Runtime.Repl.ExecuteLine($"{name}=inside Write-Output \"seen:$env:{name}\" > out.txt".Replace("out.txt", Path.Combine(t.Home, "out.txt"), StringComparison.Ordinal), echo: false);
        Assert.Equal("seen:inside", File.ReadAllText(Path.Combine(t.Home, "out.txt")).Trim());
        Assert.Null(Environment.GetEnvironmentVariable(name));
        Assert.Equal(["False"], t.Run($"[bool](Get-Variable __saved_{name} -ErrorAction Ignore)"));
    }

    [Fact]
    public void DevNullAndHistoryExpansionRunThroughTheRepl()
    {
        using var t = TestPickle.Create(start: true);
        var result = t.Runtime.Repl.ExecuteLine("Get-Item /definitely/missing 2>/dev/null", echo: false);
        Assert.Contains("→ Get-Item /definitely/missing 2>$null", t.Terminal.GetScreenText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Cannot find path", t.Terminal.GetScreenText(), StringComparison.Ordinal);

        t.Runtime.Repl.ExecuteLine("Write-Output first-command", echo: false);
        var outcome = t.Runtime.Translation.Translate("!!", t.Runtime.Engine.CurrentDirectory);
        Assert.True(outcome.Changed);
        Assert.Equal("Write-Output first-command", outcome.Command);
        Assert.NotNull(result);
    }

    [Fact]
    public void DisabledTranslationLeavesLinesAlone()
    {
        using var t = TestPickle.Create(start: true, configure: c => c.Translation.Enabled = false);
        var outcome = t.Runtime.Translation.Translate("export A=1", "/");
        Assert.False(outcome.Changed);
        Assert.Equal("export A=1", outcome.Command);
    }

    [Fact]
    public void RewritersCanBeDisabledByName()
    {
        using var t = TestPickle.Create(start: true, configure: c => c.Translation.Disabled = ["devnull"]);
        Assert.False(t.Runtime.Translation.Translate("cmd 2>/dev/null", "/").Changed);
        Assert.True(t.Runtime.Translation.Translate("export A=1", "/").Changed);
    }

    [Fact]
    public void FailingRewriterIsSkipped()
    {
        using var t = TestPickle.Create(start: true);
        t.Runtime.TranslationRegistry.RegisterRewriter(new ThrowingRewriter());
        Assert.Equal("$env:A = '1'", t.Runtime.Translation.Translate("export A=1", "/").Command);
    }

    [Fact]
    public void RegistryShimsAreDefined()
    {
        using var t = TestPickle.Create();
        t.Runtime.TranslationRegistry.RegisterShim(new TranslationShim("pkshimtest", "test shim", "'shim:' + ($args -join ',')"));
        t.Runtime.TranslationRegistry.RegisterShim(new TranslationShim("bad name", "invalid", "'x'"));
        t.Runtime.InitializeComponents();
        t.Runtime.Start([]);
        Assert.Equal(["shim:a,b"], t.Run("pkshimtest a b"));
        Assert.DoesNotContain("bad name", t.Runtime.Translation.BuildShimScript(), StringComparison.Ordinal);
    }

    [Fact]
    public void PkTranslateStatusListAndToggle()
    {
        using var t = TestPickle.Create(start: true);
        Assert.Equal(["True", OperatingSystem.IsWindows().ToString()], t.Run("$s = pk translate status; $s.Enabled; $s.ShimsLoaded"));
        var list = t.Run("pk translate list | ForEach-Object { \"$($_.Kind):$($_.Name)\" }");
        Assert.Contains("rewriter:export", list);
        Assert.Contains("shim:grep", list);

        t.Runtime.Repl.ExecuteLine("pk translate off", echo: false);
        Assert.False(t.Runtime.Config.Current.Translation.Enabled);
        t.Runtime.Repl.ExecuteLine("pk translate on --shims", echo: false);
        Assert.True(t.Runtime.Config.Current.Translation.Enabled);
        Assert.True(((TranslationPipeline)t.Runtime.Translation).ShimsLoaded);
        Assert.Equal(["Function"], t.Run("(Get-Command grep).CommandType.ToString()"));
    }

    private sealed class ThrowingRewriter : IInputRewriter
    {
        public string Name => "throws";

        public int Order => 1;

        public RewriteResult? Rewrite(string input, RewriteContext context) => throw new InvalidOperationException("broken plugin");
    }
}

public class CommandNotFoundTests
{
    [Fact]
    public void SuggestsCloseCommandNames()
    {
        Assert.Equal(["git"], CommandNotFound.Suggest("gti", ["git", "gitk", "go", "vim"]));
        Assert.Equal(["Get-ChildItem"], CommandNotFound.Suggest("Get-ChilItem", ["Get-ChildItem", "Get-Item", "Set-Item"]));
        Assert.Empty(CommandNotFound.Suggest("zzzzzz", ["git", "ls"]));
    }

    [Fact]
    public void WingetHintOnWindowsForKnownTools()
    {
        var hints = CommandNotFound.BuildHints("ffmpeg", [], suggestions: true, offerWinget: true, isWindows: true, wizardWingetId: null, hasPickleWinget: true);
        Assert.Equal(["💡 'ffmpeg' isn't installed. Install it: pk winget install Gyan.FFmpeg"], hints);

        var fromWizard = CommandNotFound.BuildHints("mytool.exe", [], true, true, true, "Vendor.MyTool", hasPickleWinget: false);
        Assert.Equal(["💡 'mytool' isn't installed. Install it: winget install --id Vendor.MyTool -e"], fromWizard);

        Assert.Empty(CommandNotFound.BuildHints("ffmpeg", [], true, true, isWindows: false, null, true));
        Assert.Empty(CommandNotFound.BuildHints("ffmpeg", [], true, offerWinget: false, isWindows: true, null, true));
    }

    [Fact]
    public void PrintsDidYouMeanForTypedCommands()
    {
        using var t = TestPickle.Create(start: true, width: 120);
        t.Runtime.Repl.ExecuteLine("Get-ChilItem", echo: false);
        var screen = t.Terminal.GetScreenText();
        Assert.Contains("did you mean Get-ChildItem?", screen, StringComparison.Ordinal);
        Assert.Single(screen.Split('\n'), l => l.Contains("did you mean", StringComparison.Ordinal));
    }

    [Fact]
    public void IgnoresLookupsInsideScriptsAndHonorsConfig()
    {
        using var t = TestPickle.Create(start: true, width: 120);
        t.Runtime.Repl.ExecuteLine("& { Get-ChilItem }", echo: false);
        Assert.DoesNotContain("did you mean", t.Terminal.GetScreenText(), StringComparison.Ordinal);

        using var off = TestPickle.Create(start: true, width: 120, configure: c => c.Shell.CommandNotFoundSuggestions = false);
        off.Runtime.Repl.ExecuteLine("Get-ChilItem", echo: false);
        Assert.DoesNotContain("did you mean", off.Terminal.GetScreenText(), StringComparison.Ordinal);
    }
}
