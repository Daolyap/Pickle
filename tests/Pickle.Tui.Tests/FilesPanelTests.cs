using Pickle.Abstractions;
using Pickle.Tui.Panels.Files;
using Terminal.Gui.App;
using Terminal.Gui.Input;

namespace Pickle.Tui.Tests;

public sealed class FilesPanelTests : IDisposable
{
    private readonly string _root = TuiHarness.TempDir();

    public FilesPanelTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub dir"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, "alpha.txt"), "line one\nline two\n");
        File.WriteAllText(Path.Combine(_root, "beta.md"), "# Beta\n");
        File.WriteAllText(Path.Combine(_root, "sub dir", "gamma.cs"), "class Gamma {}\n");
        File.WriteAllText(Path.Combine(_root, ".hidden"), "secret\n");
        File.WriteAllText(Path.Combine(_root, "node_modules", "dep.js"), "x\n");
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), [1, 0, 2, 0, 3]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static bool Scanned(IApplication app) =>
        app.TopRunnableView is FilesPanel { IsScanning: false } panel && panel.List.TotalCount > 0;

    [Fact]
    public void ScannerSkipsHiddenAndIgnoredDirectoriesByDefault()
    {
        var entries = new List<FileEntry>();
        FileScanner.Scan(_root, new FileScanOptions(), entries.AddRange, CancellationToken.None);
        Assert.Equal(["alpha.txt", "beta.md", "blob.bin", "sub dir", Path.Combine("sub dir", "gamma.cs")], entries.Select(e => e.RelativePath));

        entries.Clear();
        FileScanner.Scan(_root, new FileScanOptions { IncludeHidden = true, SkipIgnored = false, DirectoriesOnly = true }, entries.AddRange, CancellationToken.None);
        Assert.Equal([".git", "node_modules", "sub dir"], entries.Select(e => e.RelativePath));
    }

    [Fact]
    public void FilterSelectAndEnterInsertsTheRelativeQuotedPath()
    {
        var script = new UiScript()
            .WaitFor("scanned", Scanned)
            .Type("gamma")
            .WaitFor("filtered", app => TuiHarness.Top<FilesPanel>(app).List.Selected?.Display.EndsWith("gamma.cs", StringComparison.Ordinal) == true)
            .WaitFor("preview", app => TuiHarness.Top<FilesPanel>(app).Preview.PlainText.Contains("class Gamma", StringComparison.Ordinal))
            .Do("check line numbers", app => Assert.True(TuiHarness.Top<FilesPanel>(app).Preview.LineNumbers))
            .Press(Key.Enter);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        t.Runtime.Shell.SetLocation(_root);

        var result = host.Show(FilesPanelPlugin.PanelId);

        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.InsertText, "'" + Path.Combine("sub dir", "gamma.cs") + "'"), result);
    }

    [Fact]
    public void SpaceMarksSeveralFilesAndArgumentSetsTheRoot()
    {
        var script = new UiScript()
            .WaitFor("scanned", Scanned)
            .Do("title shows root", app => Assert.Contains(_root, TuiHarness.Top<FilesPanel>(app).PanelTitle, StringComparison.Ordinal))
            .Press(Key.Space)
            .Press(Key.Space)
            .Press(Key.Enter);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;

        var result = host.Show(FilesPanelPlugin.PanelId, _root);

        script.AssertOk();
        Assert.Equal(PanelResultKind.InsertText, result?.Kind);
        Assert.Equal($"{Path.Combine(_root, "alpha.txt")} {Path.Combine(_root, "beta.md")}", result!.Text);
    }

    [Fact]
    public void DirectoryModeChangesToTheSelectedDirectory()
    {
        var script = new UiScript()
            .WaitFor("scanned", Scanned)
            .Do("only directories", app => Assert.All(TuiHarness.Top<FilesPanel>(app).List.VisibleItems, e => Assert.True(e.IsDirectory)))
            .Type("sub")
            .Press(Key.Enter);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;

        var result = host.Show(FilesPanelPlugin.PanelId, FilesPanel.DirectoryModePrefix + _root);

        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.ChangeDirectory, Path.Combine(_root, "sub dir")), result);
    }

    [Fact]
    public void PreviewDescribesDirectoriesAndBinaries()
    {
        var listing = FilePreview.Build(Path.Combine(_root, "sub dir"));
        Assert.Equal(["gamma.cs  15 B"], listing.Lines.Select(l => l.Text));

        var binary = FilePreview.Build(Path.Combine(_root, "blob.bin"));
        Assert.Contains(binary.Lines, l => l.Text.StartsWith("Type      binary", StringComparison.Ordinal));
        Assert.False(binary.LineNumbers);
    }

    [Theory]
    [InlineData("/work/src/a.cs", "/work", "src/a.cs")]
    [InlineData("/work/my file.txt", "/work", "'my file.txt'")]
    [InlineData("/other/it's.txt", "/work", "'/other/it''s.txt'")]
    [InlineData("/work", "/work", ".")]
    public void FormatsPathsRelativeToTheCurrentDirectory(string path, string cwd, string expected)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(expected, FilesPanel.FormatPaths([path], cwd));
    }
}
