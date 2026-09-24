using Pickle.Abstractions;
using Pickle.Core.Translation;
using Pickle.Testing;

namespace Pickle.Core.Tests.Translation;

public sealed class ShimModuleTests : IDisposable
{
    private readonly TestPickle _t;
    private readonly string _dir;

    public ShimModuleTests()
    {
        _t = TestPickle.Create(start: true);
        Assert.True(((TranslationPipeline)_t.Runtime.Translation).LoadShims(force: true));
        _dir = Directory.CreateTempSubdirectory("pickle-shims").FullName;
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "alpha\nbeta\ngamma\nAlpha two\n");
        File.WriteAllText(Path.Combine(_dir, "b.log"), "one\ntwo\nthree\n");
        Directory.CreateDirectory(Path.Combine(_dir, "sub", "deep"));
        File.WriteAllText(Path.Combine(_dir, "sub", "c.txt"), "alpha in sub\n");
        File.WriteAllText(Path.Combine(_dir, "sub", "deep", "d.md"), "# doc\n");
        _t.Run($"Set-Location -LiteralPath '{_dir}'");
    }

    public void Dispose()
    {
        _t.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private IReadOnlyList<string> Run(string script) => _t.Run(script);

    private ShellResult RunWithErrors(string script) => _t.Runtime.Shell.InvokeAsync(script).GetAwaiter().GetResult();

    [Fact]
    public void ModuleExportsExactlyTheCatalog()
    {
        var exported = Run("(Get-Module Pickle.Translate).ExportedFunctions.Keys | Sort-Object");
        Assert.Equal(ShimCatalog.Shims.Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase), exported);
    }

    [Fact]
    public void Ls()
    {
        Assert.Equal(["a.txt", "b.log", "sub"], Run("ls -1"));
        Assert.Equal(["sub", "b.log", "a.txt"], Run("ls -1 -r"));
        Assert.Equal(["a.txt", "b.log"], Run("ls -1 *.*"));
        Assert.Equal(["a.txt", "b.log", "sub"], Run("(ls).Name"));
        Assert.Contains(Run("ls -l"), l => l.EndsWith(" a.txt", StringComparison.Ordinal) && l.Contains("24", StringComparison.Ordinal));
        Assert.Contains(Path.Combine("sub", "deep", "d.md"), Run("ls -1R"));
        Assert.Equal(["a.txt"], Run("ls -1S | Select-Object -First 1"));
        Assert.Single(RunWithErrors("ls nope").Errors);
    }

    [Fact]
    public void Grep()
    {
        Assert.Equal(["alpha"], Run("grep alpha a.txt"));
        Assert.Equal(["alpha", "Alpha two"], Run("grep -i alpha a.txt"));
        Assert.Equal(["1:alpha", "4:Alpha two"], Run("grep -in alpha a.txt"));
        Assert.Equal(["beta", "gamma", "Alpha two"], Run("grep -v '^alpha$' a.txt"));
        Assert.Equal(["2"], Run("grep -ic alpha a.txt"));
        Assert.Equal(["a.txt", Path.Combine("sub", "c.txt")], Run("grep -rl alpha ."));
        Assert.Equal([Path.Combine("sub", "c.txt") + ":alpha in sub"], Run("grep -r --include=*.txt 'in sub'"));
        Assert.Equal(["beta", "gamma"], Run("grep -E 'beta|gamma' a.txt"));
        Assert.Empty(Run("grep 'beta|gamma' a.txt"));
        Assert.Equal(["x.y"], Run("'x.y','xzy' | grep -F x.y"));
        Assert.Equal(["two"], Run("'one','two','network' | grep -w two"));
        Assert.Equal(["a.txt:alpha", "b.log:two"], Run("grep -H alpha a.txt; grep two b.log -H"));
        Assert.Equal(["beta"], Run("grep -e beta a.txt"));
    }

    [Fact]
    public void GrepPipelineSeesFormattedObjects()
    {
        var output = Run("Get-Item a.txt, b.log | grep b.log");
        Assert.Single(output);
        Assert.Contains("b.log", output[0], StringComparison.Ordinal);
        Assert.Equal(["1"], Run("'x' | grep nomatch; $LASTEXITCODE"));
    }

    [Fact]
    public void CatHeadTailWc()
    {
        Assert.Equal(["alpha", "beta", "gamma", "Alpha two"], Run("cat a.txt"));
        Assert.Equal(["     1\tone", "     2\ttwo", "     3\tthree"], Run("cat -n b.log"));
        Assert.Equal(["alpha", "beta"], Run("head -n 2 a.txt"));
        Assert.Equal(["alpha"], Run("head -1 a.txt"));
        Assert.Equal(["alp"], Run("head -c 3 a.txt"));
        Assert.Equal(["gamma", "Alpha two"], Run("tail -n 2 a.txt"));
        Assert.Equal(["three"], Run("tail -1 b.log"));
        Assert.Equal(["two", "three"], Run("tail -n +2 b.log"));
        Assert.Equal(["1", "2"], Run("1..5 | head -n 2"));
        Assert.Equal(["4", "5"], Run("1..5 | tail -n 2"));
        Assert.Equal(["4"], Run("wc -l a.txt"));
        Assert.Equal(["5"], Run("1..5 | wc -l"));
        Assert.Equal(["3", "3", "14", "b.log"], Run("wc b.log | ForEach-Object { $_.Lines; $_.Words; $_.Bytes; $_.File }"));
        Assert.Equal(["7"], Run("(wc -l a.txt b.log | Where-Object File -eq total).Lines"));
    }

    [Fact]
    public void Find()
    {
        Assert.Equal(["./a.txt", "./sub/c.txt"], Run("find . -name '*.txt'"));
        Assert.Equal(["./sub", "./sub/deep"], Run("find . -type d -mindepth 1"));
        Assert.Equal(["./a.txt", "./b.log"], Run("find . -maxdepth 1 -type f"));
        Assert.Equal(["./a.txt"], Run("find . -iname 'A.TXT'"));
        Assert.Single(RunWithErrors("find . -bogus").Errors);
    }

    [Fact]
    public void TouchMkdirCpMvRm()
    {
        Run("touch new.txt; mkdir -p x/y/z; cp a.txt x/; cp -r sub x/copy; mv b.log x/y/moved.log");
        Assert.True(File.Exists(Path.Combine(_dir, "new.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "x", "a.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "x", "copy", "deep", "d.md")));
        Assert.True(File.Exists(Path.Combine(_dir, "x", "y", "moved.log")));
        Assert.False(File.Exists(Path.Combine(_dir, "b.log")));

        Run("touch -d '2020-01-02 03:04:05' new.txt");
        Assert.Equal(2020, File.GetLastWriteTime(Path.Combine(_dir, "new.txt")).Year);

        Assert.Single(RunWithErrors("mkdir x").Errors);
        Assert.Single(RunWithErrors("mkdir q/r").Errors);
        Assert.Single(RunWithErrors("cp sub nowhere").Errors);

        File.WriteAllText(Path.Combine(_dir, "keep.txt"), "original");
        Run("cp -n a.txt keep.txt");
        Assert.Equal("original", File.ReadAllText(Path.Combine(_dir, "keep.txt")));

        Assert.Single(RunWithErrors("rm x").Errors);
        Assert.Equal(["removed 'new.txt'"], Run("rm -v new.txt"));
        Run("rm -rf x");
        Assert.False(Directory.Exists(Path.Combine(_dir, "x")));
        Assert.Empty(RunWithErrors("rm -f does-not-exist").Errors);
        Assert.Single(RunWithErrors("rm does-not-exist").Errors);
    }

    [Fact]
    public void RmRefusesRootsHomeAndDots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = RunWithErrors($"rm -rf / ; rm -rf '{home}' ; rm -rf ~ ; rm -rf .");
        Assert.Equal(4, result.Errors.Count);
        Assert.Contains("filesystem root", result.Errors[0].ToString(), StringComparison.Ordinal);
        Assert.Contains("home directory", result.Errors[1].ToString(), StringComparison.Ordinal);
        Assert.Contains("home directory", result.Errors[2].ToString(), StringComparison.Ordinal);
        Assert.True(Directory.Exists(home));
        Assert.True(File.Exists(Path.Combine(_dir, "a.txt")));

        var parent = Path.GetDirectoryName(home.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parent) && parent != Path.GetPathRoot(parent))
        {
            Assert.Contains("contains your home", RunWithErrors($"rm -rf '{parent}'").Errors.Single().ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DuDf()
    {
        Assert.Equal(["."], Run("(du -s .).Path"));
        var bytes = long.Parse(Run("(du -s .).Bytes")[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(new[] { "a.txt", "b.log", "sub/c.txt", "sub/deep/d.md" }.Sum(f => new FileInfo(Path.Combine(_dir, f)).Length), bytes);
        Assert.Equal(["./sub/deep", "./sub", "."], Run("(du .).Path"));
        Assert.Equal(["./sub", "."], Run("(du -d 1 .).Path"));
        Assert.Equal(["19"], Run("(du -sh sub).Size"));
        Assert.NotEmpty(Run("(df -h .).MountedOn"));
    }

    [Fact]
    public void WhichTypeUnameEnvUnset()
    {
        Assert.Equal(["Get-ChildItem: cmdlet (Microsoft.PowerShell.Management)"], Run("which Get-ChildItem"));
        Assert.Equal(["grep is a function (Pickle.Translate)"], Run("type grep"));
        Assert.Equal(["Linux"], Run("uname"));
        Assert.Equal(3, Run("uname -snm")[0].Split(' ').Length);
        Assert.Equal(["PICKLE_SHIM_T=1"], Run("env PICKLE_SHIM_T=1 | Where-Object { $_ -like 'PICKLE_SHIM_T=*' }"));
        Assert.Equal(["yes", ""], Run("env PICKLE_SHIM_U=yes pwsh-less-check 2>$null; $x = & { $env:PICKLE_SHIM_U = 'yes'; $env:PICKLE_SHIM_U }; $x; unset PICKLE_SHIM_U; [string]$env:PICKLE_SHIM_U"));
    }

    [Fact]
    public void PosixAliasCreatesPickleAlias()
    {
        Run("alias hi='Write-Output hello'");
        Assert.Equal("Write-Output hello", _t.Runtime.Aliases.Get("hi")?.Body);
        Assert.Equal(["hello"], Run("hi"));
        Assert.Equal(["alias hi='Write-Output hello'"], Run("alias hi"));
        Run("unalias hi");
        Assert.Null(_t.Runtime.Aliases.Get("hi"));
    }

    [Fact]
    public void RemovesConflictingAliasesAndRestoresThemOnUnload()
    {
        using var t = TestPickle.Create(start: true);
        t.Run("Set-Alias -Name cat -Value Get-Content -Option AllScope -Scope Global -Force");
        var pipeline = (TranslationPipeline)t.Runtime.Translation;
        pipeline.LoadShims(force: true);
        Assert.Equal(["Function"], t.Run("(Get-Command cat).CommandType.ToString()"));

        t.Runtime.Config.Update(c => c.Translation.Enabled = false);
        pipeline.Reconcile();
        Assert.Equal(["Alias"], t.Run("(Get-Command cat).CommandType.ToString()"));
        Assert.False(pipeline.ShimsLoaded);
    }

    [Fact]
    public void DisabledShimsAreNotExported()
    {
        using var t = TestPickle.Create(start: true, configure: c => c.Translation.Disabled = ["grep", "ls"]);
        ((TranslationPipeline)t.Runtime.Translation).LoadShims(force: true);
        var exported = t.Run("(Get-Module Pickle.Translate).ExportedFunctions.Keys");
        Assert.DoesNotContain("grep", exported);
        Assert.DoesNotContain("ls", exported);
        Assert.Contains("cat", exported);
    }

    [Fact]
    public void PreferNativeBinariesSkipsShimsFoundOnPath()
    {
        using var t = TestPickle.Create(start: true, configure: c => c.Translation.PreferNativeBinaries = true);
        ((TranslationPipeline)t.Runtime.Translation).LoadShims(force: true);
        var exported = t.Run("(Get-Module Pickle.Translate).ExportedFunctions.Keys");
        Assert.DoesNotContain("ls", exported);
        Assert.Contains("alias", exported);
    }

    [Fact]
    public void ShimsAreNotLoadedOnLinuxByDefault()
    {
        using var t = TestPickle.Create(start: true);
        Assert.False(((TranslationPipeline)t.Runtime.Translation).ShimsLoaded);
        Assert.Equal(["False"], t.Run("[bool](Get-Module Pickle.Translate)"));
    }
}
