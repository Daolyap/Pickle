using Pickle.Modules.Explain;
using Pickle.Testing;

namespace Pickle.Modules.Tests.Explain;

public class ExplainTests
{
    private static readonly ExplainPack Pack = ExplainPack.Load();

    private static async Task<Explanation> Explain(string line, Func<string, CancellationToken, Task<ShellCommandInfo?>>? shell = null) =>
        await new CommandExplainer(Pack, shell).ExplainAsync(line);

    private static string[] Lines(Explanation e) => [.. e.Parts.Select(p => $"{p.Text} => {p.Meaning}")];

    [Fact]
    public void ThePackCoversTheCommonCommands()
    {
        foreach (var name in new[] { "ls", "grep", "find", "tar", "curl", "git", "docker", "kubectl", "systemctl", "rsync", "chmod", "ssh", "apt", "sudo" })
        {
            Assert.NotNull(Pack.Find(name));
        }

        Assert.True(Pack.Commands.Count >= 60);
        Assert.NotNull(Pack.Find("tar.exe"));
        Assert.Contains("commit", Pack.Find("git")!.Subcommands.Keys);
    }

    [Fact]
    public void SplitHonorsQuotesNestingAndTheOperators()
    {
        var pieces = CommandExplainer.Split("cat 'a|b' | grep \"x && y\" && echo $(ls | wc -l) ; true &> /dev/null");

        Assert.Equal(["cat 'a|b'", "|", "grep \"x && y\"", "&&", "echo $(ls | wc -l)", ";", "true &> /dev/null"], pieces.Select(p => p.Text));
        Assert.Equal([false, true, false, true, false, true, false], pieces.Select(p => p.IsOperator));
        Assert.Equal("a|b", pieces[0].Words[1].Value);
    }

    [Fact]
    public async Task CombinedShortFlagsAreExplainedOneByOneAndTheLastTakesItsValue()
    {
        var e = await Explain("tar -xzvf backup.tgz -C /tmp");

        Assert.Equal(
            [
                "tar => Create or extract archives",
                "-x => extract an archive",
                "-z => compress with gzip (.tar.gz)",
                "-v => verbose: list files as they are processed",
                "-f backup.tgz => the archive file name (must be followed by it): backup.tgz",
                "-C /tmp => change to this directory first: /tmp",
            ],
            Lines(e));
        Assert.Empty(e.Warnings);
    }

    [Fact]
    public async Task SubcommandsFlagsWithAttachedValuesAndLongFlagsWithEquals()
    {
        var e = await Explain("git commit -am \"fix bug\" --author=ada");

        Assert.Equal(
            [
                "git => Version control",
                "commit => Record the staged changes",
                "-a => also stage all tracked files that changed",
                "-m \"fix bug\" => the commit message: fix bug",
                "--author=ada => an option of git that the pack does not describe (run 'git --help')",
            ],
            Lines(e));
        Assert.Equal("the commit message: 3", (await Explain("head -n3 file")).Parts[1].Meaning.Replace("how many lines (default 10)", "the commit message"));
    }

    [Fact]
    public async Task PipesAndRedirectionsAreExplained()
    {
        var e = await Explain("ls -la /etc | grep -i ssh > out.txt 2>&1");

        Assert.Contains(e.Parts, p => p is { Text: "|", Kind: PartKind.Operator });
        Assert.Contains(e.Parts, p => p.Text == "> out.txt" && p.Meaning.StartsWith("write the output to out.txt", StringComparison.Ordinal));
        Assert.Contains(e.Parts, p => p.Text == "2>&1" && p.Meaning.Contains("same place", StringComparison.Ordinal));
        Assert.Contains(e.Parts, p => p.Text == "-i" && p.Meaning == "ignore case");
        Assert.Contains(e.Parts, p => p.Text == "/etc" && p.Meaning.StartsWith("files or directories to list", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WrappersExplainTheCommandTheyRun()
    {
        var e = await Explain("sudo -u deploy systemctl restart nginx.service");

        Assert.Equal(
            [
                "sudo => Run a command with another user's rights (root by default)",
                "-u deploy => run as this user: deploy",
                "systemctl => Control systemd services",
                "restart => Stop and start a unit",
                "nginx.service => an argument",
            ],
            Lines(e));
        Assert.Equal([0, 1, 1, 2, 2], e.Parts.Select(p => p.Depth));
    }

    [Fact]
    public async Task EnvironmentAssignmentsAndSubstitutionsAreRecognized()
    {
        var e = await Explain("FOO=1 BAR=2 echo $(date) done");

        Assert.Equal(PartKind.Assignment, e.Parts[0].Kind);
        Assert.Contains("set FOO for this command only", e.Parts[0].Meaning, StringComparison.Ordinal);
        Assert.Contains(e.Parts, p => p.Text == "$(date)" && p.Meaning.Contains("substitutes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("755", "permissions 755: owner can read, write and run; group can read and run; others can read and run (rwxr-xr-x)")]
    [InlineData("640", "permissions 640: owner can read and write; group can read; others can nothing (rw-r-----)")]
    [InlineData("u+x", "owner gets run permission")]
    [InlineData("go-w", "group, others loses write permission")]
    [InlineData("a=r", "everyone is set to exactly read permission")]
    public void ChmodModesAreDecoded(string mode, string expected) => Assert.Equal(expected, CommandExplainer.DescribeMode(mode));

    [Fact]
    public void NonModesAreNotDecoded() => Assert.Null(CommandExplainer.DescribeMode("file.txt"));

    [Theory]
    [InlineData("rm -rf /", "deletes recursively")]
    [InlineData("rm -rf ~", "deletes recursively")]
    [InlineData("curl -s https://get.example.com | sudo sh", "Piping a download")]
    [InlineData("chmod -R 777 /var/www", "777")]
    [InlineData("git push --force origin main", "force")]
    [InlineData("dd if=/dev/zero of=/dev/sda bs=1M", "device")]
    [InlineData("find . -name '*.log' -delete", "-delete")]
    [InlineData(":(){ :|:& };:", "fork bomb")]
    [InlineData("docker run --privileged -v /:/host alpine", "isolation")]
    [InlineData("git reset --hard HEAD~3", "permanently")]
    [InlineData("rsync -a --delete src/ dst/", "--dry-run")]
    public async Task DangerousCommandsGetWarnings(string line, string expectedFragment)
    {
        var e = await Explain(line);

        Assert.Contains(e.Warnings, w => w.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("ls -la")]
    [InlineData("git status")]
    [InlineData("rm file.txt")]
    [InlineData("curl -L https://example.com/file -o file")]
    [InlineData("chmod 644 notes.txt")]
    [InlineData("docker run -d -p 8080:80 nginx")]
    public async Task OrdinaryCommandsGetNoWarnings(string line) => Assert.Empty((await Explain(line)).Warnings);

    [Fact]
    public async Task UnknownProgramsAreNeverRunAndSaySo()
    {
        var e = await Explain("mytool --fast input.dat");

        Assert.Equal(PartKind.Command, e.Parts[0].Kind);
        Assert.Contains("never runs unknown programs", e.Parts[0].Meaning, StringComparison.Ordinal);
        Assert.Equal(["an option", "an argument"], e.Parts.Skip(1).Select(p => p.Meaning));
    }

    [Fact]
    public async Task PowerShellAliasesAndCmdletsAreResolvedThroughTheShell()
    {
        var calls = new List<string>();
        Task<ShellCommandInfo?> Shell(string name, CancellationToken ct)
        {
            calls.Add(name);
            return Task.FromResult<ShellCommandInfo?>(name == "gci"
                ? new ShellCommandInfo("Alias", "Get-ChildItem", "Microsoft.PowerShell.Management", new Dictionary<string, string> { ["Path"] = "String[]", ["Recurse"] = "SwitchParameter", ["Filter"] = "String" })
                : null);
        }

        var e = await Explain("gci -Recurse -Filter *.cs C:\\src", Shell);

        Assert.Equal(["gci"], calls);
        Assert.Equal("alias for Get-ChildItem (Microsoft.PowerShell.Management)", e.Parts[0].Meaning);
        Assert.Equal("parameter Recurse (switch: on/off)", e.Parts[1].Meaning);
        Assert.Equal("parameter Filter (String): *.cs", e.Parts[2].Meaning);
        Assert.Equal("a path", e.Parts[3].Meaning);
    }

    [Fact]
    public void TheRealShellDescribesCmdlets()
    {
        using var t = TestPickle.Create(start: true, modules: OptionalModules.All, machineModules: ["explain"]);

        var lines = t.Run("pk explain Get-Date -Format yyyy | ForEach-Object { \"$($_.Part.Trim())=$($_.Meaning)\" }");

        Assert.Contains(lines, l => l.StartsWith("Get-Date=cmdlet from Microsoft.PowerShell.Utility", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("-Format yyyy=parameter Format (String)", StringComparison.Ordinal));
    }

    [Fact]
    public void YourOwnPackFilesExtendAndOverrideTheBuiltInOnes()
    {
        var dir = Directory.CreateTempSubdirectory("pickle-explain").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "mine.json"), """{ "deploy": { "summary": "Deploy my app", "flags": { "--env": "=target environment" } }, "ls": { "summary": "My own ls" } }""");
            File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json");
            var errors = new List<string>();

            var pack = ExplainPack.Load(dir, (file, _) => errors.Add(Path.GetFileName(file)));

            Assert.Equal("Deploy my app", pack.Find("deploy")!.Summary);
            Assert.Equal("My own ls", pack.Find("ls")!.Summary);
            Assert.NotNull(pack.Find("grep"));
            Assert.Equal(["broken.json"], errors);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ThePkCommandAndTheKeyBindingAreRegistered()
    {
        using var t = TestPickle.Create(start: true, modules: OptionalModules.All, machineModules: ["explain"]);

        Assert.Equal(["tar => Create or extract archives"], t.Run("pk explain 'tar -x' | Select-Object -First 1 | ForEach-Object { \"$($_.Part.Trim()) => $($_.Meaning)\" }"));
        Assert.NotNull(t.Runtime.KeyBindingRegistry.GetAction("explain.line"));
        Assert.Equal("explain.line", t.Runtime.KeyBindingRegistry.Bindings["Alt+Shift+E"]);
        Assert.NotNull(t.Runtime.PanelRegistry.Get("explain"));
    }
}
