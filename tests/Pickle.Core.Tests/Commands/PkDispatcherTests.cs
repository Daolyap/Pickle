using Pickle.Abstractions;
using Pickle.Core.Commands;
using Pickle.Testing;

namespace Pickle.Core.Tests.Commands;

public class PkDispatcherTests
{
    private sealed class EchoArgsCommand : IPickleCommand
    {
        public string Name => "echo-args";

        public string Description => "Echo arguments";

        public string Usage => "pk echo-args [args]";

        public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            context.WriteObject(string.Join('|', args));
            return ValueTask.FromResult(0);
        }
    }

    /// <summary>Awaits off the pipeline thread, then needs the runspace and the output stream again.</summary>
    private sealed class BackgroundThenRunspaceCommand(PickleRuntime runtime) : IPickleCommand
    {
        public string Name => "bg-runspace";

        public string Description => "Test";

        public string Usage => "pk bg-runspace";

        public async ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            await Task.Run(() => Thread.Sleep(20), cancellationToken).ConfigureAwait(false);
            var result = await MainRunspace.InvokeAsync(runtime, "param($n) $n * 2", new Dictionary<string, object?> { ["n"] = 21 }, cancellationToken).ConfigureAwait(false);
            context.WriteObject(result.Output[0].BaseObject);
            context.WriteHost("from a pool thread");
            return 0;
        }
    }

    /// <summary>Like <see cref="BackgroundThenRunspaceCommand"/> but through the public <see cref="IPickleShell"/>.</summary>
    private sealed class ShellFromWorkerCommand : IPickleCommand
    {
        public string Name => "shell-worker";

        public string Description => "Test";

        public string Usage => "pk shell-worker";

        public async ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            await Task.Run(() => Thread.Sleep(20), cancellationToken).ConfigureAwait(false);
            Assert.True(context.Pickle.Shell.IsBusy);
            var result = await context.Pickle.Shell.InvokeAsync("6 * 7", cancellationToken: cancellationToken).ConfigureAwait(false);
            context.WriteObject(result.Output[0].BaseObject);
            return 0;
        }
    }

    [Fact]
    public async Task CommandsRunByScriptsCanUseTheShellFromOtherThreads()
    {
        // Not typed at the prompt (a key handler or hook runs it through InvokeAsync), so IsExecuting is false.
        using var t = TestPickle.Create(start: true);
        t.Runtime.CommandRegistry.Register(new ShellFromWorkerCommand());
        var result = await t.Runtime.Shell.InvokeAsync("pk shell-worker", cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Empty(result.Errors);
        Assert.Equal(42, Assert.Single(result.Output).BaseObject);
        Assert.False(t.Runtime.Shell.IsBusy);
    }

    [Fact]
    public void PkAloneAndPkHelpListCommands()
    {
        using var t = TestPickle.Create(start: true);
        t.Run("pk");
        Assert.Contains("Pickle commands", t.Terminal.RawOutput, StringComparison.Ordinal);
        Assert.Contains("config", t.Terminal.RawOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandHelpFlagPrintsUsageGenerically()
    {
        using var t = TestPickle.Create(start: true);
        t.Run("pk config --help");
        Assert.Contains("pk config set <path> <value> [--local]", t.Terminal.RawOutput, StringComparison.Ordinal);
        t.Terminal.ClearRawOutput();
        t.Run("pk help sync");
        Assert.Contains("pk sync init <folder|git-url>", t.Terminal.RawOutput, StringComparison.Ordinal);
        t.Terminal.ClearRawOutput();
        t.Run("pk version -h");
        Assert.Contains("Show Pickle, PowerShell and .NET versions", t.Terminal.RawOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpGroupsCommandsAndFitsTheTerminalWidth()
    {
        using var t = TestPickle.Create(width: 60, height: 80, start: true);
        t.Run("pk help");
        var lines = Pickle.Abstractions.TextWidth.StripAnsi(t.Terminal.RawOutput).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        Assert.All(lines, line => Assert.True(Pickle.Abstractions.TextWidth.VisibleWidth(line) < 60, line));
        Assert.Contains("Shell", lines);
        Assert.Contains("Keys", lines);
        Assert.Contains(lines, l => l.TrimStart().StartsWith("history", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Ctrl+R History search", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("cnfig", "pk config")]
    [InlineData("plugins", "pk plugin")]
    [InlineData("doctr", "pk doctor")]
    public async Task UnknownCommandSuggestsTheClosestOne(string typed, string suggestion)
    {
        using var t = TestPickle.Create(start: true);
        var result = await t.Runtime.Shell.InvokeAsync($"pk {typed}");
        var error = Assert.Single(result.Errors);
        Assert.Contains($"Unknown command 'pk {typed}'", error.ToString(), StringComparison.Ordinal);
        Assert.Contains($"Did you mean '{suggestion}'?", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentsThatLookLikeParametersReachTheCommandUnchanged()
    {
        using var t = TestPickle.Create(start: true);
        t.Runtime.CommandRegistry.Register(new EchoArgsCommand());
        Assert.Equal(["-n|5"], t.Run("pk echo-args -n 5"));
        Assert.Equal(["-Name|x|-na|--local"], t.Run("pk echo-args -Name x -na --local"));
        Assert.Equal(["-v|-d|-ea|stop"], t.Run("pk echo-args -v -d -ea stop"));
        Assert.Equal(["a b|c"], t.Run("pickle echo-args 'a b' c"));
        Assert.Equal(["-n|5"], t.Run("Invoke-PickleCommand echo-args -n 5"));
    }

    [Fact]
    public async Task CommandsCanUseTheRunspaceAndOutputFromOtherThreads()
    {
        using var t = TestPickle.Create(start: true);
        t.Runtime.CommandRegistry.Register(new BackgroundThenRunspaceCommand(t.Runtime));
        var output = await Task.Run(() => t.Run("pk bg-runspace")).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["42"], output);
        Assert.Contains("from a pool thread", t.Terminal.RawOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCodeIsExposedAsLastExitCode()
    {
        using var t = TestPickle.Create(start: true);
        await t.Runtime.Shell.InvokeAsync("pk config nope");
        Assert.Equal(["2"], t.Run("$global:LASTEXITCODE"));
        t.Run("pk version | Out-Null");
        Assert.Equal(["0"], t.Run("$global:LASTEXITCODE"));
    }

    [Fact]
    public void FuzzySuggestsCloseNamesOnly()
    {
        Assert.Equal(["config"], DidYouMean.Suggest("cofnig", ["config", "plugin", "sync"]));
        Assert.Equal(["editor.bellStyle"], DidYouMean.Suggest("bellstyle", ["editor.bellStyle", "theme"]));
        Assert.Empty(DidYouMean.Suggest("zzzzzz", ["config", "plugin"]));
    }
}
