using Pickle.Abstractions;
using Pickle.Core.Translation;
using Pickle.Core.Translation.Rewriters;

namespace Pickle.Core.Tests.Translation;

public class RewriterTests
{
    private static readonly RewriteContext NoHistory = new("/work", []);

    private static string? Apply(IInputRewriter rewriter, string input, RewriteContext? context = null) =>
        rewriter.Rewrite(input, context ?? NoHistory)?.Rewritten;

    [Theory]
    [InlineData("FOO=bar ./run.sh", "$__saved_FOO = $env:FOO; try { $env:FOO = 'bar'; ./run.sh } finally { $__pickle_ok = $?; $env:FOO = $__saved_FOO; Remove-Variable __saved_FOO }; if ($__pickle_ok) { Remove-Variable __pickle_ok } else { Remove-Variable __pickle_ok; Write-Error 'failed' -ErrorAction Ignore }")]
    [InlineData("A=1 B='x y' cmd arg", "$__saved_A = $env:A; $__saved_B = $env:B; try { $env:A = '1'; $env:B = 'x y'; cmd arg } finally { $__pickle_ok = $?; $env:A = $__saved_A; $env:B = $__saved_B; Remove-Variable __saved_A, __saved_B }; if ($__pickle_ok) { Remove-Variable __pickle_ok } else { Remove-Variable __pickle_ok; Write-Error 'failed' -ErrorAction Ignore }")]
    [InlineData("DEBUG=1 npm start | grep x", "$__saved_DEBUG = $env:DEBUG; try { $env:DEBUG = '1'; npm start | grep x } finally { $__pickle_ok = $?; $env:DEBUG = $__saved_DEBUG; Remove-Variable __saved_DEBUG }; if ($__pickle_ok) { Remove-Variable __pickle_ok } else { Remove-Variable __pickle_ok; Write-Error 'failed' -ErrorAction Ignore }")]
    [InlineData("A=\"$HOME/x\" cmd; echo done", "$__saved_A = $env:A; try { $env:A = \"$HOME/x\"; cmd } finally { $__pickle_ok = $?; $env:A = $__saved_A; Remove-Variable __saved_A }; if ($__pickle_ok) { Remove-Variable __pickle_ok } else { Remove-Variable __pickle_ok; Write-Error 'failed' -ErrorAction Ignore }; echo done")]
    [InlineData("X=$Y cmd", "$__saved_X = $env:X; try { $env:X = $env:Y; cmd } finally { $__pickle_ok = $?; $env:X = $__saved_X; Remove-Variable __saved_X }; if ($__pickle_ok) { Remove-Variable __pickle_ok } else { Remove-Variable __pickle_ok; Write-Error 'failed' -ErrorAction Ignore }")]
    public void EnvPrefix(string input, string expected) => Assert.Equal(expected, Apply(new EnvPrefixRewriter(isWindows: false), input));

    [Theory]
    [InlineData("A=1")]
    [InlineData("echo A=1 cmd")]
    [InlineData("'A=1 cmd'")]
    [InlineData("\"A=1 cmd\"")]
    [InlineData("$a=1")]
    [InlineData("Get-Process | Where-Object { $_.X -eq 'A=1 cmd' }")]
    [InlineData("git config user.name=x")]
    [InlineData("$m = @\"\nsay \"hi\nFOO=1 make\n\"@")]
    public void EnvPrefixNoOps(string input) => Assert.Null(Apply(new EnvPrefixRewriter(isWindows: false), input));

    [Theory]
    [InlineData("export FOO=bar", "$env:FOO = 'bar'")]
    [InlineData("export A=1 B=two", "$env:A = '1'; $env:B = 'two'")]
    [InlineData("export GREETING=\"hello $USER\"", "$env:GREETING = \"hello $env:USER\"")]
    [InlineData("export P=$PATH:/opt/bin", "$env:P = \"${env:PATH}:/opt/bin\"")]
    [InlineData("export FOO", "$env:FOO = $FOO")]
    [InlineData("export", "Get-ChildItem Env:")]
    [InlineData("export -p", "Get-ChildItem Env:")]
    [InlineData("unset FOO", "Remove-Item -LiteralPath Env:FOO -ErrorAction Ignore")]
    [InlineData("unset A B", "Remove-Item -LiteralPath Env:A, Env:B -ErrorAction Ignore")]
    [InlineData("unset -f myfn", "Remove-Item -LiteralPath Function:myfn -ErrorAction Ignore")]
    [InlineData("source ./env.ps1", ". ./env.ps1")]
    [InlineData("export A=1 && make", "Set-Item -LiteralPath Env:A -Value '1' && make")]
    [InlineData("export A=1; make", "$env:A = '1'; make")]
    [InlineData("export B=~/bin", "$env:B = \"$HOME/bin\"")]
    [InlineData("export Q='it''s'", "$env:Q = 'it''s'")]
    [InlineData("$t = @'\nit's\n'@\nexport A=1", "$t = @'\nit's\n'@\n$env:A = '1'")]
    public void Export(string input, string expected) => Assert.Equal(expected, Apply(new ExportRewriter(isWindows: false), input));

    [Theory]
    [InlineData("export PATH=$PATH:/usr/local/bin", "$env:PATH = \"$env:PATH$([IO.Path]::PathSeparator)/usr/local/bin\"")]
    [InlineData("export PATH=/opt/bin:$PATH", "$env:PATH = \"/opt/bin$([IO.Path]::PathSeparator)$env:PATH\"")]
    [InlineData("export PATH=C:\\tools:$PATH", "$env:PATH = \"C:\\tools$([IO.Path]::PathSeparator)$env:PATH\"")]
    [InlineData("export URL=http://x", "$env:URL = 'http://x'")]
    public void ExportPathListsOnWindows(string input, string expected) => Assert.Equal(expected, Apply(new ExportRewriter(isWindows: true), input));

    [Theory]
    [InlineData("echo export")]
    [InlineData("'export A=1'")]
    [InlineData("export -n A")]
    [InlineData("exporter A=1")]
    [InlineData("source")]
    [InlineData("$msg = @'\ndon't touch\nexport A=1\n'@")]
    [InlineData("$msg = @'\r\nit's\r\nexport A=1\r\n'@; Write-Output $msg")]
    public void ExportNoOps(string input) => Assert.Null(Apply(new ExportRewriter(isWindows: false), input));

    [Theory]
    [InlineData("cmd >/dev/null", "cmd >$null")]
    [InlineData("cmd 2>/dev/null", "cmd 2>$null")]
    [InlineData("cmd &>/dev/null", "cmd *>$null")]
    [InlineData("cmd > /dev/null", "cmd >$null")]
    [InlineData("cmd 2> /dev/null", "cmd 2>$null")]
    [InlineData("cmd >/dev/null 2>&1", "cmd *>$null")]
    [InlineData("cmd 1>/dev/null", "cmd >$null")]
    [InlineData("a 2>/dev/null | b >/dev/null", "a 2>$null | b >$null")]
    [InlineData("Write-Output hi 2>/dev/null", "Write-Output hi 2>$null")]
    public void DevNull(string input, string expected) => Assert.Equal(expected, Apply(new DevNullRewriter(), input));

    [Theory]
    [InlineData("echo '>/dev/null'")]
    [InlineData("echo \"2>/dev/null\"")]
    [InlineData("cp /dev/null file")]
    [InlineData("& { cmd 2>/dev/null }")]
    [InlineData("cat file")]
    [InlineData("@'\nit's >/dev/null\n'@ | Set-Content x")]
    public void DevNullNoOps(string input) => Assert.Null(Apply(new DevNullRewriter(), input));

    [Theory]
    [InlineData("sudo !!", "sudo apt install vim")]
    [InlineData("!!", "apt install vim")]
    [InlineData("vim !$", "vim vim")]
    [InlineData("!! | grep x", "apt install vim | grep x")]
    public void HistoryExpansion(string input, string expected) =>
        Assert.Equal(expected, Apply(new HistoryExpansionRewriter(), input, new RewriteContext("/", ["ls", "apt install vim"])));

    [Theory]
    [InlineData("echo '!!'")]
    [InlineData("echo \"!!\"")]
    [InlineData("if (!!$x) { 1 }")]
    [InlineData("!!$x")]
    [InlineData("echo hi")]
    public void HistoryExpansionNoOps(string input) =>
        Assert.Null(Apply(new HistoryExpansionRewriter(), input, new RewriteContext("/", ["ls"])));

    [Fact]
    public void HistoryExpansionWithoutHistoryIsUntouched() => Assert.Null(Apply(new HistoryExpansionRewriter(), "!!"));

    [Fact]
    public void HistoryExpansionResolvesChainedDesignators() =>
        Assert.Equal("echo one two", Apply(new HistoryExpansionRewriter(), "!!", new RewriteContext("/", ["echo one", "!! two"])));

    [Theory]
    [InlineData("apt install ffmpeg", "pk winget install ffmpeg")]
    [InlineData("sudo apt-get install -y ffmpeg jq", "pk winget install ffmpeg; pk winget install jq")]
    [InlineData("brew install --cask firefox", "pk winget install firefox")]
    [InlineData("pacman -S git", "pk winget install git")]
    [InlineData("apt search ripgrep", "winget search ripgrep")]
    [InlineData("pacman -Ss ripgrep", "winget search ripgrep")]
    [InlineData("sudo apt update && sudo apt upgrade -y", "pk upgrade")]
    [InlineData("brew update; brew upgrade", "pk upgrade")]
    [InlineData("pacman -Syu", "pk upgrade")]
    [InlineData("apt remove vim", "winget uninstall vim")]
    [InlineData("apt update", "winget source update")]
    [InlineData("dnf update", "pk upgrade")]
    [InlineData("apt install x && echo ok", "pk winget install x && echo ok")]
    public void PackageManagers(string input, string expected) =>
        Assert.Equal(expected, Apply(new PackageManagerRewriter(isWindows: true, _ => false, _ => true), input));

    [Fact]
    public void PackageManagersFallBackToPlainWinget() =>
        Assert.Equal("winget install ffmpeg", Apply(new PackageManagerRewriter(true, _ => false, _ => false), "apt install ffmpeg"));

    [Fact]
    public void PackageManagerUpgradeFallsBackToWingetUpgradeAll() =>
        Assert.Equal("winget upgrade --all", Apply(new PackageManagerRewriter(true, _ => false, _ => false), "apt upgrade"));

    [Theory]
    [InlineData("apt install ffmpeg", false, false)]
    [InlineData("choco install git", true, true)]
    [InlineData("echo apt install x", true, false)]
    [InlineData("apt install $pkg", true, false)]
    [InlineData("'apt install x'", true, false)]
    public void PackageManagerNoOps(string input, bool isWindows, bool exists) =>
        Assert.Null(Apply(new PackageManagerRewriter(isWindows, _ => exists, _ => true), input));

    [Fact]
    public void SudoOnPowerShellCommandOpensElevatedPickle()
    {
        var rewriter = new SudoRewriter(true, n => n == "Remove-Item" ? new CommandLookup("Cmdlet", null) : null, null, @"C:\Pickle\pickle.exe");
        var result = rewriter.Rewrite("sudo Remove-Item 'C:\\x y'", new RewriteContext(@"C:\work", []));
        Assert.NotNull(result);
        Assert.StartsWith("Start-Process -Verb RunAs -FilePath 'C:\\Pickle\\pickle.exe' -ArgumentList '-NoLogo -c \"Set-Location -LiteralPath ''C:\\work''; try { Remove-Item ''C:\\x y'' }", result.Rewritten, StringComparison.Ordinal);
        Assert.EndsWith("finally { Read-Host ''Press Enter to close'' }\"'", result.Rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void SudoLeavesNativeCommandsToWindowsSudo()
    {
        const string sudoExe = @"C:\Windows\System32\sudo.exe";
        var rewriter = new SudoRewriter(true, n => n == "sudo" ? new CommandLookup("Application", sudoExe) : new CommandLookup("Application", @"C:\x\" + n + ".exe"), sudoExe, "pickle");
        Assert.Null(rewriter.Rewrite("sudo netsh winsock reset", NoHistory));
    }

    [Fact]
    public void SudoAloneOpensElevatedShell()
    {
        var rewriter = new SudoRewriter(true, _ => null, null, "pickle.exe");
        Assert.Equal("Start-Process -Verb RunAs -FilePath 'pickle.exe'", rewriter.Rewrite("sudo -i", NoHistory)?.Rewritten);
    }

    [Theory]
    [InlineData("sudo ls", false)]
    [InlineData("echo sudo", true)]
    [InlineData("sudo -u bob ls", true)]
    public void SudoNoOps(string input, bool isWindows) =>
        Assert.Null(new SudoRewriter(isWindows, _ => null, null, "pickle").Rewrite(input, NoHistory));

    [Fact]
    public void SudoRespectsUserProvidedSudo() =>
        Assert.Null(new SudoRewriter(true, n => n == "sudo" ? new CommandLookup("Function", null) : null, null, "pickle").Rewrite("sudo Get-Date", NoHistory));

    [Fact]
    public void WindowsArgumentQuoting()
    {
        Assert.Equal("plain", PowerShellText.WindowsArgument("plain"));
        Assert.Equal("\"a b\"", PowerShellText.WindowsArgument("a b"));
        Assert.Equal("\"say \\\"hi\\\"\"", PowerShellText.WindowsArgument("say \"hi\""));
        Assert.Equal("\"C:\\dir with space\\\\\"", PowerShellText.WindowsArgument("C:\\dir with space\\"));
    }

    [Fact]
    public void LexerKeepsStringsAndBlocksTogether()
    {
        var segments = ShellLexer.Segments("a 'b c' \"d;e\" { f; g } && h | i # comment ; j");
        Assert.Equal(3, segments.Count);
        Assert.Equal(["a", "'b c'", "\"d;e\"", "{ f; g }"], segments[0].Words.Select(w => w.Text));
        Assert.Equal(ShellSeparator.AndAnd, segments[0].After);
        Assert.Equal(["i"], segments[2].Words.Select(w => w.Text));
        Assert.Equal("b c", segments[0].Words[1].Literal);
        Assert.Null(segments[0].Words[3].Literal);
    }
}
