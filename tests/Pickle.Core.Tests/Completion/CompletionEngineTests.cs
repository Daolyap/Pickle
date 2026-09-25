using System.Management.Automation.Runspaces;
using Pickle.Abstractions;
using Pickle.Core.Completion;
using Pickle.Testing;

namespace Pickle.Core.Tests.Completion;

public class CompletionEngineTests
{
    [Fact]
    public async Task CompletesCommandNames()
    {
        using var t = Started();
        var set = await Complete(t, "Get-Chil");
        var item = Assert.Single(set.Items, i => i.CompletionText == "Get-ChildItem");
        Assert.Equal(CompletionKind.Command, item.Kind);
        Assert.Equal(0, set.ReplacementIndex);
        Assert.Equal(8, set.ReplacementLength);
    }

    [Fact]
    public async Task CompletesParametersWithTheirType()
    {
        using var t = Started();
        var set = await Complete(t, "Get-ChildItem -Fo");
        var force = Assert.Single(set.Items, i => i.CompletionText == "-Force");
        Assert.Equal(CompletionKind.Parameter, force.Kind);
        Assert.Equal("Force", force.ListText);
        Assert.Equal("[switch]", force.Description);
        Assert.Equal(14, set.ReplacementIndex);
    }

    [Fact]
    public async Task CompletesFilePathsAndDirectories()
    {
        using var t = Started();
        // Not deleted afterwards: SetLocation also moves the process-wide current directory, which parallel tests share.
        var dir = Directory.CreateTempSubdirectory("pickle-complete").FullName;
        File.WriteAllText(Path.Combine(dir, "alpha.txt"), string.Empty);
        File.WriteAllText(Path.Combine(dir, "alphabet.md"), string.Empty);
        Directory.CreateDirectory(Path.Combine(dir, "alps"));
        t.Runtime.Engine.SetLocation(dir);

        var set = await Complete(t, "Get-Content ./alph");
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal([$".{sep}alpha.txt", $".{sep}alphabet.md"], set.Items.Select(i => i.CompletionText));
        Assert.All(set.Items, i => Assert.Equal(CompletionKind.File, i.Kind));
        Assert.All(set.Items, i => Assert.Null(i.Description));

        var dirs = await Complete(t, "Set-Location ./al");
        Assert.Contains(dirs.Items, i => i.CompletionText == $".{sep}alps" && i.Kind == CompletionKind.Directory);
    }

    [Fact]
    public async Task CompletesVariables()
    {
        using var t = Started();
        var set = await Complete(t, "$PSVer");
        var item = Assert.Single(set.Items);
        Assert.Equal("$PSVersionTable", item.CompletionText);
        Assert.Equal(CompletionKind.Variable, item.Kind);
        Assert.StartsWith("[PSVersionHashTable]", item.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyWhenNothingMatches()
    {
        using var t = Started();
        var set = await Complete(t, "Get-NoSuchThingAnywhere");
        Assert.Empty(set.Items);
        Assert.Equal(23, set.ReplacementIndex);
    }

    [Fact]
    public async Task MergesProvidersWithTheSameReplacementRange()
    {
        using var t = Started();
        t.Runtime.CompletionRegistry.Register(new FakeProvider("hi", 50, (0, 8), new CompletionItem("Get-Chilly", "Get-Chilly", CompletionKind.Text, "fake")));
        t.Runtime.CompletionRegistry.Register(new FakeProvider("lo", -5, (0, 8), new CompletionItem("Get-ChildItem", "dup", CompletionKind.Text), new CompletionItem("Get-Chilled", "Get-Chilled", CompletionKind.Text)));
        t.Runtime.CompletionRegistry.Register(new FakeProvider("other-range", 90, (4, 4), new CompletionItem("nope", "nope", CompletionKind.Text)));

        var set = await Complete(t, "Get-Chil");
        Assert.Equal(["Get-Chilly", "Get-ChildItem", "Get-Chilled"], set.Items.Select(i => i.CompletionText));
        Assert.Equal(CompletionKind.Command, set.Items[1].Kind);
    }

    [Fact]
    public async Task UsesTheHighestPriorityProviderWhenPowerShellHasNothing()
    {
        using var t = Started();
        t.Runtime.CompletionRegistry.Register(new FakeProvider("a", 5, (0, 4), new CompletionItem("zzzA", "zzzA", CompletionKind.Text)));
        t.Runtime.CompletionRegistry.Register(new FakeProvider("b", 7, (0, 2), new CompletionItem("zzB", "zzB", CompletionKind.Text)));
        t.Runtime.CompletionRegistry.Register(new FakeProvider("c", 1, (0, 2), new CompletionItem("zzC", "zzC", CompletionKind.Text)));

        var set = await Complete(t, "zzzz");
        Assert.Equal(["zzB", "zzC"], set.Items.Select(i => i.CompletionText));
        Assert.Equal(2, set.ReplacementLength);
    }

    [Fact]
    public async Task AFailingProviderIsIgnored()
    {
        using var t = Started();
        t.Runtime.CompletionRegistry.Register(new ThrowingProvider());
        var set = await Complete(t, "Get-Chil");
        Assert.Contains(set.Items, i => i.CompletionText == "Get-ChildItem");
    }

    [Fact]
    public async Task PkCompletesItsSubcommandsOnly()
    {
        using var t = Started();
        var set = await Complete(t, "pk ");
        Assert.Contains(set.Items, i => i.CompletionText == "history" && i.Description is not null);
        Assert.Contains(set.Items, i => i.CompletionText == "version");
        Assert.All(set.Items, i => Assert.Equal(CompletionKind.Command, i.Kind));
        Assert.Equal((3, 0), (set.ReplacementIndex, set.ReplacementLength));

        var partial = await Complete(t, "Get-Date; pickle his");
        Assert.Equal(["history"], partial.Items.Select(i => i.CompletionText));
        Assert.Equal((17, 3), (partial.ReplacementIndex, partial.ReplacementLength));

        var help = await Complete(t, "pk help ver");
        Assert.Equal(["version"], help.Items.Select(i => i.CompletionText));

        Assert.DoesNotContain((await Complete(t, "pk version ")).Items, i => i.CompletionText == "history");
    }

    [Theory]
    [InlineData("pk", null)]
    [InlineData("pk ", "")]
    [InlineData("pk hi", "hi")]
    [InlineData("pk history ", null)]
    [InlineData("pk help hi", "hi")]
    [InlineData("$x = pk v", "v")]
    [InlineData("Write-Output pk ", null)]
    [InlineData("pk | pk ", "")]
    public void FindsThePkSubcommandWord(string input, string? expected)
    {
        var word = PickleCommandCompletionProvider.FindSubcommandWord(input, input.Length);
        Assert.Equal(expected, word?.Prefix);
    }

    [Fact]
    public async Task CompletesPickleAliasesWithDescriptions()
    {
        using var t = Started();
        t.Runtime.Aliases.Set(new AliasDefinition { Name = "gcozz", Body = "git checkout", Description = "Check out a branch" });
        t.Runtime.Aliases.Set(new AliasDefinition { Name = "gczzlog", Body = "git log --oneline" });
        var set = await Complete(t, "gc");
        Assert.Equal(["gcozz", "gczzlog"], set.Items.Select(i => i.CompletionText).Take(2));
        Assert.Equal(CompletionKind.Alias, set.Items[0].Kind);
        Assert.Equal("Check out a branch", set.Items[0].Description);
        Assert.Equal("→ git log --oneline", set.Items[1].Description);
    }

    [Fact]
    public async Task BusyRunspaceReturnsProviderResultsOnly()
    {
        using var t = Started();
        t.Runtime.CompletionRegistry.Register(new FakeProvider("p", 1, (0, 8), new CompletionItem("Get-Chilly", "Get-Chilly", CompletionKind.Text)));
        var busy = t.Runtime.Shell.InvokeAsync("Start-Sleep -Milliseconds 1500");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (t.Runtime.Engine.MainRunspace.RunspaceAvailability != RunspaceAvailability.Busy && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        var set = await Complete(t, "Get-Chil");
        Assert.Equal(["Get-Chilly"], set.Items.Select(i => i.CompletionText));
        await busy;

        var after = await Complete(t, "Get-Chil");
        Assert.Contains(after.Items, i => i.CompletionText == "Get-ChildItem");
    }

    [Fact]
    public async Task TimesOutAndLeavesTheRunspaceUsable()
    {
        using var t = Started();
        t.Run("function global:Get-SlowThing { param([ArgumentCompleter({ Start-Sleep -Seconds 20; 'x' })] $Name) }");
        var engine = (CompletionEngine)t.Runtime.Completion;
        engine.Timeout = TimeSpan.FromMilliseconds(300);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var set = await Complete(t, "Get-SlowThing -Name ");
        Assert.Empty(set.Items);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.Equal(["ok"], t.Run("'ok'"));
    }

    [Fact]
    public void CommonPrefixExtendsTheTypedWordOnly()
    {
        var set = new CompletionSet(0, 3, [Item("Get-ChildItem"), Item("Get-Clipboard"), Item("Get-Command")]);
        Assert.Equal("Get-C", CompletionEngine.CommonPrefix("get", set));
        Assert.Null(CompletionEngine.CommonPrefix("get-c", set with { ReplacementLength = 5 }));

        var quoted = new CompletionSet(0, 2, [Item("'./my file.txt'"), Item("'./my other.txt'")]);
        Assert.Null(CompletionEngine.CommonPrefix("./", quoted));
    }

    [Fact]
    public async Task CompleteActionInsertsASingleMatch()
    {
        using var t = Started();
        var buffer = new FakeEditorBuffer("Get-ChildI | Out-Null", cursor: 10);
        await Action(t, EditorActionNames.Complete)(buffer, CancellationToken.None);
        Assert.Equal("Get-ChildItem | Out-Null", buffer.Text);
        Assert.Equal(13, buffer.Cursor);
        Assert.Null(buffer.Overlay);
        Assert.False(buffer.Accepted);
    }

    [Fact]
    public async Task CompleteActionInsertsTheCommonPrefix()
    {
        using var t = Started();
        t.Runtime.CompletionRegistry.Register(new FakeProvider("p", 1, (0, 3), Item("zzzabc1"), Item("zzzabc2")));
        var buffer = new FakeEditorBuffer("zzz");
        await Action(t, EditorActionNames.Complete)(buffer, CancellationToken.None);
        Assert.Equal("zzzabc", buffer.Text);
        Assert.Null(buffer.Overlay);
    }

    [Fact]
    public async Task CompleteActionOpensTheMenuWhenAmbiguous()
    {
        using var t = Started();
        var buffer = new FakeEditorBuffer("Get-ChildItem -Fo");
        await Action(t, EditorActionNames.Complete)(buffer, CancellationToken.None);
        var menu = Assert.IsType<CompletionMenu>(buffer.Overlay);
        Assert.Equal("-Force", menu.Selected?.CompletionText);
        Assert.Equal("Get-ChildItem -Fo", buffer.Text);

        buffer.Press("Tab", "Enter");
        Assert.Equal("Get-ChildItem -FollowSymlink", buffer.Text);
        Assert.False(buffer.Accepted);
    }

    [Fact]
    public async Task CompletePreviousSelectsTheLastItem()
    {
        using var t = Started();
        var buffer = new FakeEditorBuffer("Get-ChildItem -Fo");
        await Action(t, EditorActionNames.CompletePrevious)(buffer, CancellationToken.None);
        var menu = Assert.IsType<CompletionMenu>(buffer.Overlay);
        Assert.Equal("-FollowSymlink", menu.Selected?.CompletionText);
    }

    [Fact]
    public async Task WithoutTheMenuTabCyclesInline()
    {
        using var t = Started(c => c.Editor.CompletionMenu = false);
        var buffer = new FakeEditorBuffer("Get-ChildItem -Fo");
        var complete = Action(t, EditorActionNames.Complete);
        await complete(buffer, CancellationToken.None);
        Assert.Equal("Get-ChildItem -Force", buffer.Text);
        await complete(buffer, CancellationToken.None);
        Assert.Equal("Get-ChildItem -FollowSymlink", buffer.Text);
        await complete(buffer, CancellationToken.None);
        Assert.Equal("Get-ChildItem -Force", buffer.Text);
        await Action(t, EditorActionNames.CompletePrevious)(buffer, CancellationToken.None);
        Assert.Equal("Get-ChildItem -FollowSymlink", buffer.Text);
        Assert.Null(buffer.Overlay);
    }

    [Theory]
    [InlineData("'/tmp/My Folder'", "'/tmp/My Folder/'", 16)]
    [InlineData("\"/tmp/My Folder\"", "\"/tmp/My Folder/\"", 16)]
    [InlineData("'/tmp/it''s'", "'/tmp/it''s/'", 12)]
    [InlineData("/tmp/src", "/tmp/src/", 9)]
    [InlineData("/tmp/src/", "/tmp/src/", 9)]
    [InlineData("'/tmp/open", "'/tmp/open/", 11)]
    public void DirectoriesKeepTheCursorInsideTheQuotes(string completion, string inserted, int cursor)
    {
        var (text, at) = CompletionEngine.Insertion(new CompletionItem(completion, completion, CompletionKind.Directory));
        Assert.Equal(inserted, text);
        Assert.Equal(cursor, at);
    }

    [Fact]
    public void FilesAreInsertedAsIs()
    {
        Assert.Equal(("'/tmp/a b.txt'", 14), CompletionEngine.Insertion(new CompletionItem("'/tmp/a b.txt'", "a b.txt", CompletionKind.File)));
    }

    [Fact]
    public async Task TabKeepsCompletingInsideAQuotedDirectory()
    {
        using var t = Started();
        var root = Directory.CreateTempSubdirectory("pickle-quoted").FullName;
        Directory.CreateDirectory(Path.Combine(root, "My Folder", "Sub Dir"));
        var complete = Action(t, EditorActionNames.Complete);

        var buffer = new FakeEditorBuffer($"Get-ChildItem '{Path.Combine(root, "My Fo")}");
        await complete(buffer, CancellationToken.None);
        var sep = Path.DirectorySeparatorChar;
        var first = $"Get-ChildItem '{Path.Combine(root, "My Folder")}{sep}'";
        Assert.Equal(first, buffer.Text);
        Assert.Equal(first.Length - 1, buffer.Cursor);

        await complete(buffer, CancellationToken.None);
        var second = $"Get-ChildItem '{Path.Combine(root, "My Folder", "Sub Dir")}{sep}'";
        Assert.Equal(second, buffer.Text);
        Assert.Equal(second.Length - 1, buffer.Cursor);
    }

    private static TestPickle Started(Action<PickleConfig>? configure = null) => TestPickle.Create(start: true, configure: configure);

    private static Task<CompletionSet> Complete(TestPickle t, string input) =>
        t.Runtime.Completion.CompleteAsync(new CompletionRequest(input, input.Length, t.Runtime.Engine.CurrentDirectory));

    private static Func<IEditorBuffer, CancellationToken, Task> Action(TestPickle t, string name)
    {
        var action = t.Runtime.KeyBindingRegistry.GetAction(name) ?? throw new InvalidOperationException(name);
        return (buffer, ct) => action.Handler(buffer, ct).AsTask();
    }

    private static CompletionItem Item(string text) => new(text, text, CompletionKind.Text);

    private sealed class FakeProvider(string name, int priority, (int Index, int Length) range, params CompletionItem[] items) : ICompletionProvider
    {
        public string Name => name;

        public int Priority => priority;

        public ValueTask<CompletionSet?> GetCompletionsAsync(CompletionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<CompletionSet?>(new CompletionSet(range.Index, range.Length, items));
    }

    private sealed class ThrowingProvider : ICompletionProvider
    {
        public string Name => "throws";

        public int Priority => 3;

        public ValueTask<CompletionSet?> GetCompletionsAsync(CompletionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}
