using Pickle.Core.Hosting;
using Pickle.Testing;

namespace Pickle.Core.Tests.Hosting;

public class TabProgressTests
{
    private static readonly string Spinner = TabProgress.Sequence(3, 0);
    private static readonly string Cleared = TabProgress.Sequence(0, 0);
    private static readonly string Failed = TabProgress.Sequence(2, 100);

    [Fact]
    public void CommandsSpinTheTabAndFailuresTurnItRedUntilTheNextCommand()
    {
        using var t = TestPickle.Create(start: true, configure: c => c.Terminal.TabProgress = "on");

        t.Runtime.Repl.ExecuteLine("Write-Output 'fine'", echo: false);
        Assert.Equal([Spinner, Cleared], Sequences(t));

        t.Runtime.Repl.ExecuteLine("Get-Item -LiteralPath '/definitely/missing'", echo: false);
        Assert.Equal([Spinner, Cleared, Spinner, Failed], Sequences(t));

        t.Runtime.Repl.ExecuteLine("Write-Output 'again'", echo: false);
        Assert.Equal([Spinner, Cleared, Spinner, Failed, Spinner, Cleared], Sequences(t));
    }

    [Fact]
    public void WriteProgressShowsItsPercentage()
    {
        using var t = TestPickle.Create(start: true, configure: c => c.Terminal.TabProgress = "on");

        t.Runtime.Repl.ExecuteLine(
            "Write-Progress -Activity copy -PercentComplete 40; Write-Progress -Activity copy -PercentComplete 90; Write-Progress -Activity copy -Completed",
            echo: false);

        Assert.Equal([Spinner, TabProgress.Sequence(1, 40), TabProgress.Sequence(1, 90), Spinner, Cleared], Sequences(t));
    }

    [Fact]
    public void OffAndAutoOutsideWindowsTerminalWriteNothing()
    {
        using var off = TestPickle.Create(start: true, configure: c => c.Terminal.TabProgress = "off");
        off.Runtime.Repl.ExecuteLine("Get-Item -LiteralPath '/definitely/missing'", echo: false);
        Assert.Empty(Sequences(off));

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ConEmuPID")))
        {
            using var auto = TestPickle.Create(start: true);
            auto.Runtime.Repl.ExecuteLine("Write-Output 'x'", echo: false);
            Assert.Empty(Sequences(auto));
        }
    }

    [Fact]
    public void LongCommandsRingTheBell()
    {
        using var t = TestPickle.Create(start: true, configure: c => c.Terminal.BellAfterSeconds = 1);

        t.Runtime.Repl.ExecuteLine("Write-Output 'quick'", echo: false);
        Assert.DoesNotContain("\a", t.Terminal.RawOutput, StringComparison.Ordinal);

        t.Runtime.Repl.ExecuteLine("Start-Sleep -Milliseconds 1100", echo: false);
        Assert.Contains("\a", t.Terminal.RawOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ResetClearsARedTab()
    {
        using var t = TestPickle.Create(start: true, configure: c => c.Terminal.TabProgress = "on");
        t.Runtime.Repl.ExecuteLine("Get-Item -LiteralPath '/definitely/missing'", echo: false);
        t.Runtime.TabProgress.Reset();
        t.Runtime.TabProgress.Reset();
        Assert.Equal([Spinner, Failed, Cleared], Sequences(t));
    }

    private static List<string> Sequences(TestPickle t)
    {
        var raw = t.Terminal.RawOutput;
        var found = new List<string>();
        for (var i = raw.IndexOf("\u001b]9;4;", StringComparison.Ordinal); i >= 0; i = raw.IndexOf("\u001b]9;4;", i + 1, StringComparison.Ordinal))
        {
            found.Add(raw[i..(raw.IndexOf('\a', i) + 1)]);
        }

        return found;
    }
}
