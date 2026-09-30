using System.ComponentModel;
using System.Management.Automation;
using Pickle.Core.Hosting;
using Pickle.Testing;

namespace Pickle.Core.Tests.Hosting;

public class ElevationOfferTests
{
    [Fact]
    public void RecognisesPermissionFailures()
    {
        Assert.True(ElevationOffer.IsPermissionFailure(Record(new UnauthorizedAccessException("nope")), null));
        Assert.True(ElevationOffer.IsPermissionFailure(Record(new Win32Exception(740)), null));
        Assert.True(ElevationOffer.IsPermissionFailure(Record(new InvalidOperationException("Service 'wuauserv' cannot be stopped: Access is denied.")), null));
        Assert.True(ElevationOffer.IsPermissionFailure(Record(new InvalidOperationException("x", new UnauthorizedAccessException())), null));
        Assert.True(ElevationOffer.IsPermissionFailure(null, 5));
        Assert.True(ElevationOffer.IsPermissionFailure(null, 740));

        Assert.False(ElevationOffer.IsPermissionFailure(Record(new ItemNotFoundException("Cannot find path 'x'")), null));
        Assert.False(ElevationOffer.IsPermissionFailure(null, 1));
        Assert.False(ElevationOffer.IsPermissionFailure(null, null));
    }

    [Fact]
    public void AnAccessDeniedCommandOffersAnElevatedRunAndNoDeclines()
    {
        using var t = Started();
        t.Terminal.Type("n");

        t.Runtime.Repl.ExecuteLine("throw [UnauthorizedAccessException]::new('Access to the path is denied.')", echo: false);

        Assert.Contains("Run it as administrator? [y/N] n", t.Terminal.GetScreenText(), StringComparison.Ordinal);
        Assert.DoesNotContain("sudo-ran", t.Terminal.GetScreenText(), StringComparison.Ordinal);
    }

    [Fact]
    public void YesRunsTheSameLineThroughSudo()
    {
        using var t = Started();
        t.Terminal.Type("y");

        t.Runtime.Repl.ExecuteLine("throw [UnauthorizedAccessException]::new('denied')", echo: false);

        Assert.Contains("sudo-ran: throw", t.Terminal.GetScreenText(), StringComparison.Ordinal);
        Assert.Equal("sudo throw [UnauthorizedAccessException]::new('denied')", t.Runtime.History.Entries[^1].CommandLine);
    }

    [Fact]
    public void SeveralStatementsAreElevatedTogether()
    {
        Assert.Equal("sudo net stop wuauserv", ElevationOffer.SudoLine(" net stop wuauserv "));
        var line = ElevationOffer.SudoLine("Stop-Service wuauserv; Remove-Item C:\\Windows\\SoftwareDistribution -Recurse");
        Assert.Equal("sudo & { Stop-Service wuauserv; Remove-Item C:\\Windows\\SoftwareDistribution -Recurse }", line);

        var rewritten = new Pickle.Core.Translation.Rewriters.SudoRewriter(true, _ => null, null, @"C:\p\pickle.exe")
            .Rewrite(line, new Pickle.Abstractions.RewriteContext(@"C:\work", []))!.Rewritten;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(rewritten, "Start-Process -Verb RunAs"));
        Assert.Contains("& { Stop-Service wuauserv; Remove-Item", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherFailuresAndElevatedSessionsAreNotAsked()
    {
        using var t = Started();
        Assert.Null(t.Runtime.Repl.ExecuteLine("Write-Output first", echo: false).ExitCode);
        t.Runtime.Repl.ExecuteLine("Get-Item -LiteralPath /definitely/missing", echo: false);
        Assert.NotNull(t.Runtime.Engine.LastNewError);
        t.Runtime.Repl.ExecuteLine("Write-Output fine", echo: false);
        Assert.Null(t.Runtime.Engine.LastNewError);
        Assert.DoesNotContain("administrator", t.Terminal.GetScreenText(), StringComparison.Ordinal);

        using var admin = TestPickle.Create(start: true, elevated: true);
        admin.Runtime.Repl.CanElevate = true;
        admin.Runtime.Repl.ExecuteLine("throw [UnauthorizedAccessException]::new('denied')", echo: false);
        Assert.DoesNotContain("Run it as administrator", admin.Terminal.GetScreenText(), StringComparison.Ordinal);
    }

    private static TestPickle Started()
    {
        var t = TestPickle.Create(start: true);
        t.Runtime.Repl.CanElevate = true;
        t.Run("function global:Stop-Thing { } ; function global:sudo { Write-Host \"sudo-ran: $args\" }");
        return t;
    }

    private static ErrorRecord Record(Exception ex) => new(ex, "Test", ErrorCategory.NotSpecified, null);
}
