using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Core.Aliases;
using Pickle.Testing;

namespace Pickle.Core.Tests.Aliases;

public class AliasTests
{
    private static AliasManager Manager(TestPickle t) => (AliasManager)t.Runtime.Aliases;

    [Fact]
    public void AddingAnIdenticalAliasAgainChangesNothing()
    {
        using var t = TestPickle.Create(start: true);
        t.Run("pk alias add ll 'Get-ChildItem -Force'");
        var first = Manager(t).Get("ll")!.UpdatedAt;
        var written = File.GetLastWriteTimeUtc(t.Paths.AliasesFile);
        var changes = 0;
        Manager(t).Changed += (_, _) => changes++;

        t.Run("pk alias add ll 'Get-ChildItem -Force'");
        Assert.Equal(first, Manager(t).Get("ll")!.UpdatedAt);
        Assert.Equal(written, File.GetLastWriteTimeUtc(t.Paths.AliasesFile));
        Assert.Equal(0, changes);

        t.Run("pk alias add ll 'Get-ChildItem -Force -Name'");
        Assert.Equal("Get-ChildItem -Force -Name", Manager(t).Get("ll")!.Body);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void SimpleAliasAppendsArguments()
    {
        using var t = TestPickle.Create(start: true);
        t.Runtime.Aliases.Set(new AliasDefinition { Name = "say", Body = "Write-Output hello" });
        Assert.Equal(["hello", "world"], t.Run("say world"));
    }

    [Fact]
    public void ParameterizedAliasBindsPlaceholdersAndDefaults()
    {
        using var t = TestPickle.Create(start: true);
        t.Runtime.Aliases.Set(new AliasDefinition { Name = "gco", Kind = AliasKind.Parameterized, Body = "Write-Output checkout {branch}" });
        t.Runtime.Aliases.Set(new AliasDefinition { Name = "serve", Kind = AliasKind.Parameterized, Body = "Write-Output port={port=8000} rest {*}" });
        t.Runtime.Aliases.Set(new AliasDefinition { Name = "mk", Kind = AliasKind.Parameterized, Body = "Write-Output v{n}.txt \"in {n}\"" });

        Assert.Equal(["checkout", "main"], t.Run("gco main"));
        Assert.Equal(["port=8000", "rest"], t.Run("serve"));
        Assert.Equal(["port=9000", "rest", "a", "b"], t.Run("serve 9000 a b"));
        Assert.Equal(["v2.txt", "in 2"], t.Run("mk 2"));
    }

    [Fact]
    public void ParameterizedAliasReportsMissingArgument()
    {
        using var t = TestPickle.Create(start: true);
        t.Runtime.Aliases.Set(new AliasDefinition { Name = "gco", Kind = AliasKind.Parameterized, Body = "git checkout {branch}" });
        var ex = Assert.Throws<InvalidOperationException>(() => t.Run("gco"));
        Assert.Contains("gco: missing required argument <branch>. Usage: gco <branch>", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptAliasIsAFunctionBody()
    {
        using var t = TestPickle.Create(start: true);
        t.Runtime.Aliases.Set(new AliasDefinition { Name = "twice", Kind = AliasKind.Script, Body = "param([int]$n) $n * 2" });
        Assert.Equal(["42"], t.Run("twice 21"));
    }

    [Fact]
    public void RemoveUndefinesFunction()
    {
        using var t = TestPickle.Create(start: true);
        t.Runtime.Aliases.Set(new AliasDefinition { Name = "gone", Body = "Write-Output x" });
        Assert.True(t.Runtime.Aliases.Remove("gone"));
        Assert.Equal(["False"], t.Run("[bool](Get-Command gone -ErrorAction Ignore)"));
        Assert.False(t.Runtime.Aliases.Remove("gone"));
    }

    [Fact]
    public void DirectoryScopedAliasFollowsLocation()
    {
        using var t = TestPickle.Create(start: true);
        var root = Directory.CreateTempSubdirectory("pickle-alias-scope").FullName;
        var inside = Directory.CreateDirectory(Path.Combine(root, "proj", "sub")).FullName;
        t.Runtime.Aliases.Set(new AliasDefinition { Name = "build", Body = "Write-Output building", DirectoryScope = Path.Combine(root, "proj") + "/**" });
        Assert.Equal(["False"], t.Run("[bool](Get-Command build -ErrorAction Ignore)"));

        t.Runtime.Repl.ExecuteLine($"Set-Location -LiteralPath '{inside}'", echo: false);
        Assert.Equal(["building"], t.Run("build"));

        t.Runtime.Repl.ExecuteLine($"Set-Location -LiteralPath '{root}'", echo: false);
        Assert.Equal(["False"], t.Run("[bool](Get-Command build -ErrorAction Ignore)"));
    }

    [Fact]
    public void MachineScopedAliasOnlyOnThatMachine()
    {
        using var t = TestPickle.Create(start: true);
        t.Runtime.Aliases.Set(new AliasDefinition { Name = "here", Body = "Write-Output here", MachineScope = Environment.MachineName });
        t.Runtime.Aliases.Set(new AliasDefinition { Name = "there", Body = "Write-Output there", MachineScope = "some-other-box" });
        Assert.Equal(["here"], t.Run("here"));
        Assert.Equal(["False"], t.Run("[bool](Get-Command there -ErrorAction Ignore)"));
    }

    [Fact]
    public void PersistsAtomicallyAndReloads()
    {
        using var t = TestPickle.Create(start: true);
        t.Runtime.Aliases.Set(new AliasDefinition { Name = "ll", Body = "Get-ChildItem -Force", Description = "long list" });
        var json = File.ReadAllText(t.Paths.AliasesFile);
        var saved = JsonSerializer.Deserialize<List<AliasDefinition>>(json, PickleJson.Options)!;
        Assert.Equal("ll", Assert.Single(saved).Name);
        Assert.Empty(Directory.GetFiles(t.Paths.ConfigDir, "*.tmp"));

        saved.Add(new AliasDefinition { Name = "bad name", Body = "x" });
        saved.Add(new AliasDefinition { Name = "hi", Body = "Write-Output hi" });
        File.WriteAllText(t.Paths.AliasesFile, JsonSerializer.Serialize(saved, PickleJson.Options));
        Assert.Equal(2, Manager(t).Reload());
        Assert.Equal(["hi"], t.Run("hi"));
    }

    [Theory]
    [InlineData("ll", true)]
    [InlineData("..", true)]
    [InlineData("k8s", true)]
    [InlineData("7z", true)]
    [InlineData("git-up", true)]
    [InlineData("bad name", false)]
    [InlineData("-x", false)]
    [InlineData(".x", false)]
    [InlineData("123", false)]
    [InlineData("a;b", false)]
    [InlineData("x$(y)", false)]
    [InlineData("foreach", false)]
    [InlineData("", false)]
    public void NameValidation(string name, bool valid) => Assert.Equal(valid, AliasNames.IsValid(name));

    [Fact]
    public void RefusesToShadowCmdletsUnlessForced()
    {
        using var t = TestPickle.Create(start: true);
        var manager = Manager(t);
        var ex = Assert.Throws<InvalidOperationException>(() => manager.SetChecked(new AliasDefinition { Name = "Get-Date", Body = "Write-Output nope" }, force: false));
        Assert.Contains("cmdlet", ex.Message, StringComparison.Ordinal);

        manager.SetChecked(new AliasDefinition { Name = "Get-Date", Body = "Write-Output forced" }, force: true);
        Assert.Equal(["forced"], t.Run("Get-Date"));
    }

    [Fact]
    public void ForcedAliasReplacesPowerShellAlias()
    {
        using var t = TestPickle.Create(start: true);
        t.Run("Set-Alias -Name zzq -Value Get-Location -Option AllScope -Scope Global");
        Assert.Throws<InvalidOperationException>(() => Manager(t).SetChecked(new AliasDefinition { Name = "zzq", Body = "Write-Output mine" }, force: false));
        Manager(t).SetChecked(new AliasDefinition { Name = "zzq", Body = "Write-Output mine" }, force: true);
        Assert.Equal(["mine"], t.Run("zzq"));
    }

    [Fact]
    public void RecursiveSimpleAliasCallsUnderlyingCommand()
    {
        using var t = TestPickle.Create(start: true);
        t.Runtime.Aliases.Set(new AliasDefinition { Name = "Get-Random", Body = "Get-Random -Minimum 5 -Maximum 6" });
        Assert.Equal(["5"], t.Run("Get-Random"));
    }

    [Fact]
    public void PkAliasCommandsWorkThroughTheRepl()
    {
        using var t = TestPickle.Create(start: true);
        t.Runtime.Repl.ExecuteLine("pk alias add greet 'Write-Output hi {who=there}'", echo: false);
        Assert.Equal(["hi", "you"], t.Run("greet you"));
        Assert.Equal(AliasKind.Parameterized, t.Runtime.Aliases.Get("greet")!.Kind);

        Assert.Equal(["greet"], t.Run("(pk alias ls).Name"));
        Assert.Contains(t.Run("Get-PickleAlias gr*").ToList(), s => s.Contains("greet", StringComparison.Ordinal) || s.Length > 0);

        t.Runtime.Repl.ExecuteLine("pk alias show greet", echo: false);
        Assert.Contains("function global:greet", t.Terminal.GetScreenText(), StringComparison.Ordinal);

        t.Runtime.Repl.ExecuteLine("pk alias add Get-Date 'Write-Output x'", echo: false);
        Assert.Contains("already an existing cmdlet", t.Terminal.GetScreenText(), StringComparison.Ordinal);

        t.Runtime.Repl.ExecuteLine("pk alias rm greet", echo: false);
        Assert.Equal(["False"], t.Run("[bool](Get-Command greet -ErrorAction Ignore)"));
    }

    [Fact]
    public void CmdletsSetGetRemove()
    {
        using var t = TestPickle.Create(start: true);
        t.Run("Set-PickleAlias -Name hey -Body 'Write-Output hey' -Description 'says hey'; $null");
        Assert.Equal(["hey"], t.Run("hey"));
        Assert.Equal(["says hey"], t.Run("(Get-PickleAlias -Name hey).Description"));
        t.Run("Remove-PickleAlias -Name hey");
        Assert.Null(t.Runtime.Aliases.Get("hey"));
        Assert.Throws<InvalidOperationException>(() => t.Run("Remove-PickleAlias -Name hey"));
        Assert.Throws<InvalidOperationException>(() => t.Run("Set-PickleAlias -Name 'bad name' -Body x"));
    }

    [Fact]
    public void EditOpensEditorAndReloads()
    {
        using var t = TestPickle.Create(start: true);
        var manager = Manager(t);
        manager.EditorLauncher = file =>
        {
            File.WriteAllText(file, """[{ "name": "edited", "body": "Write-Output edited" }]""");
            return 0;
        };

        t.Runtime.Repl.ExecuteLine("pk alias edit", echo: false);
        Assert.Equal(["edited"], t.Run("edited"));
    }

    [Theory]
    [InlineData("~/src/**", "/home/u/src", true)]
    [InlineData("~/src/**", "/home/u/src/a/b", true)]
    [InlineData("~/src/**", "/home/u/other", false)]
    [InlineData("~/src/*", "/home/u/src/a", true)]
    [InlineData("~/src/*", "/home/u/src/a/b", false)]
    [InlineData("/work/proj", "/work/proj/sub", true)]
    [InlineData("/work/proj", "/work/project", false)]
    [InlineData("**/node_modules", "/x/y/node_modules", true)]
    [InlineData("/w/**/app", "/w/a/b/app", true)]
    [InlineData("/w/**/app", "/w/app", true)]
    public void DirectoryScopeGlobs(string glob, string cwd, bool expected) =>
        Assert.Equal(expected, AliasScope.MatchesDirectory(glob, cwd, home: "/home/u", ignoreCase: false));

    [Fact]
    public void DirectoryScopeWindowsPathsAreCaseInsensitive() =>
        Assert.True(AliasScope.MatchesDirectory(@"C:\Users\Me\src\**", @"c:\users\me\SRC\app", home: @"C:\Users\Me", ignoreCase: true));
}
