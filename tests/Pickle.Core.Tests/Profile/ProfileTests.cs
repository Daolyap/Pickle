using Pickle.Abstractions;
using Pickle.Core.Profile;
using Pickle.Testing;

namespace Pickle.Core.Tests.Profile;

public class ProfileTests
{
    [Fact]
    public void ProfileVariableHasStandardProperties()
    {
        using var t = TestPickle.Create(start: true);
        Assert.Equal([t.Paths.ProfileFile], t.Run("[string]$PROFILE"));
        Assert.Equal([t.Paths.ProfileFile], t.Run("$PROFILE.CurrentUserCurrentHost"));
        Assert.Equal([t.Runtime.ProfileLoader.PwshCurrentUserAllHosts], t.Run("$PROFILE.CurrentUserAllHosts"));
        Assert.EndsWith("profile.ps1", t.Run("$PROFILE.AllUsersAllHosts")[0], StringComparison.Ordinal);
        Assert.NotEmpty(t.Run("$PROFILE.AllUsersCurrentHost")[0]);
    }

    [Fact]
    public void LoadRunsPwshProfilesThenPickleProfileAndSurvivesErrors()
    {
        using var t = TestPickle.Create(start: true, configure: c => c.Shell.LoadPwshProfile = true);
        var pwshDir = Directory.CreateDirectory(Path.Combine(t.Home, "pwsh")).FullName;
        t.Runtime.ProfileLoader.PwshProfileDirectory = pwshDir;
        File.WriteAllText(Path.Combine(pwshDir, "profile.ps1"), "$global:order = @('allhosts'); throw 'boom in pwsh profile'");
        File.WriteAllText(Path.Combine(pwshDir, "Microsoft.PowerShell_profile.ps1"), """
            $global:order += 'currenthost'
            Import-Module PSReadLine
            Set-PSReadLineOption -PredictionSource None -EditMode Windows
            function global:FromPwshProfile { 'pwsh-fn' }
            """);
        File.WriteAllText(t.Paths.ProfileFile, "$global:order += 'pickle'; $picklePs = @(Get-Module PSReadLine)[0].Version.ToString()");

        t.Runtime.ProfileLoader.Load();

        Assert.Equal(["allhosts", "currenthost", "pickle"], t.Run("$global:order"));
        Assert.Equal(["pwsh-fn"], t.Run("FromPwshProfile"));
        Assert.Equal(["2.4.5"], t.Run("$picklePs"));
        Assert.False(t.Runtime.Config.Current.Editor.Autosuggestions);
        Assert.Contains("boom in pwsh profile", t.Terminal.GetScreenText(), StringComparison.Ordinal);
    }

    [Fact]
    public void PwshProfilesAreSkippedByDefault()
    {
        using var t = TestPickle.Create(start: true);
        var pwshDir = Directory.CreateDirectory(Path.Combine(t.Home, "pwsh")).FullName;
        t.Runtime.ProfileLoader.PwshProfileDirectory = pwshDir;
        File.WriteAllText(Path.Combine(pwshDir, "profile.ps1"), "$global:ranPwsh = 'yes'");
        File.WriteAllText(t.Paths.ProfileFile, "$global:ranPickle = 'yes'");
        t.Runtime.ProfileLoader.Load();
        Assert.Equal(["", "yes"], t.Run("[string]$global:ranPwsh; $global:ranPickle"));
    }

    [Fact]
    public void FirstRunWritesTemplateOnce()
    {
        using var t = TestPickle.Create(start: true);
        t.Runtime.ProfileLoader.Load();
        Assert.Equal(ProfileLoader.Template, File.ReadAllText(t.Paths.ProfileFile));

        File.Delete(t.Paths.ProfileFile);
        t.Runtime.ProfileLoader.Load();
        Assert.False(File.Exists(t.Paths.ProfileFile));
    }

    [Fact]
    public void TemplateIsValidPowerShell()
    {
        using var t = TestPickle.Create(start: true);
        File.WriteAllText(t.Paths.ProfileFile, ProfileLoader.Template);
        t.Runtime.ProfileLoader.Load();
        Assert.True(t.Runtime.Engine.LastResult?.Success);
    }
}

public class PSReadLineShimTests
{
    private static TestPickle Start()
    {
        var t = TestPickle.Create(start: true);
        t.Runtime.ProfileLoader.ImportReadLineShim();
        return t;
    }

    [Fact]
    public void ColorsMapToThemeSyntax()
    {
        using var t = Start();
        t.Run("Set-PSReadLineOption -Colors @{ Command = 'DarkGreen'; Parameter = [ConsoleColor]::Yellow; String = \"`e[38;5;208m\"; Number = \"`e[38;2;1;2;3m\"; Variable = \"`e[96m\"; Selection = \"`e[48;5;238m\"; InlinePrediction = '#445566' }");
        var syntax = t.Runtime.Themes.Current.Syntax;
        Assert.Equal("green", syntax.Command);
        Assert.Equal("brightYellow", syntax.Parameter);
        Assert.Equal("#FF8700", syntax.String);
        Assert.Equal("#010203", syntax.Number);
        Assert.Equal("brightCyan", syntax.Variable);
        Assert.Equal("#444444", syntax.SelectionBackground);
        Assert.Equal("#445566", syntax.Suggestion);
    }

    [Fact]
    public void OptionsUpdateSessionConfig()
    {
        using var t = Start();
        t.Run("Set-PSReadLineOption -PredictionSource None -HistoryNoDuplicates:$false -MaximumHistoryCount 123 -BellStyle Visual");
        var config = t.Runtime.Config.Current;
        Assert.False(config.Editor.Autosuggestions);
        Assert.False(config.History.IgnoreDuplicates);
        Assert.Equal(123, config.History.MaxEntries);
        Assert.Equal("visual", config.Editor.BellStyle);
        Assert.Equal(["None", "123"], t.Run("$o = Get-PSReadLineOption; $o.PredictionSource; $o.MaximumHistoryCount"));

        t.Run("Set-PSReadLineOption -PredictionSource HistoryAndPlugin");
        Assert.True(config.Editor.Autosuggestions);
    }

    [Fact]
    public async Task UnsupportedOptionsWarnOnce()
    {
        using var t = Start();
        var first = await t.Runtime.Shell.InvokeAsync("Set-PSReadLineOption -EditMode Vi -BogusOption 1 3>&1 | ForEach-Object { \"$_\" }");
        var warnings = first.Output.Select(o => o.ToString()).ToList();
        Assert.Contains(warnings, w => w.Contains("-EditMode Vi", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("-BogusOption", StringComparison.Ordinal));
        Assert.Empty(first.Errors);

        var second = await t.Runtime.Shell.InvokeAsync("Set-PSReadLineOption -EditMode Vi 3>&1 | ForEach-Object { \"$_\" }");
        Assert.Empty(second.Output);
    }

    [Fact]
    public async Task KeyHandlersMapToPickleActions()
    {
        using var t = Start();
        t.Run("Set-PSReadLineKeyHandler -Chord Ctrl+f -Function ForwardWord; Set-PSReadLineKeyHandler -Key UpArrow -Function HistorySearchBackward; Set-PSReadLineKeyHandler Tab MenuComplete");
        var bindings = t.Runtime.KeyBindingRegistry.Bindings;
        Assert.Equal(EditorActionNames.ForwardWord, bindings["Ctrl+F"]);
        Assert.Equal(EditorActionNames.HistoryPrevious, bindings["UpArrow"]);
        Assert.Equal(EditorActionNames.Complete, bindings["Tab"]);
        Assert.Equal(["ForwardWord"], t.Run("(Get-PSReadLineKeyHandler -Chord Ctrl+f).Function"));

        t.Run("Remove-PSReadLineKeyHandler -Chord Ctrl+f");
        Assert.False(bindings.ContainsKey("Ctrl+F"));

        var result = await t.Runtime.Shell.InvokeAsync("Set-PSReadLineKeyHandler -Chord Ctrl+b -ScriptBlock { 'x' } 3>&1 | ForEach-Object { \"$_\" }; Set-PSReadLineKeyHandler -Chord Ctrl+q -Function ViEditVisually 3>&1 | ForEach-Object { \"$_\" }");
        Assert.Equal(2, result.Output.Count);
        Assert.False(bindings.ContainsKey("Ctrl+B"));
    }

    [Theory]
    [InlineData("Green", "brightGreen")]
    [InlineData("DarkGray", "brightBlack")]
    [InlineData("\u001b[1;31m", "red")]
    [InlineData("\u001b[38;5;4m", "blue")]
    [InlineData("\u001b[38;5;16m", "#000000")]
    [InlineData("\u001b[38;5;255m", "#EEEEEE")]
    [InlineData("brightPurple", "brightPurple")]
    [InlineData("nonsense", null)]
    public void ColorConversion(string input, string? expected) => Assert.Equal(expected, ReadLineCompat.ToPickleColor(input));
}
