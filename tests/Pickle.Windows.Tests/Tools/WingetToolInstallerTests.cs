using Pickle.Abstractions.Services;
using Pickle.Testing.Fakes;
using Pickle.Windows.Tools;

namespace Pickle.Windows.Tests.Tools;

public sealed class WingetToolInstallerTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "pickle-tool-tests");
    private static readonly string ProgramFiles = Path.Combine(Root, "Program Files");
    private static readonly ToolPackage SevenZip = new("7z", "7zip.7zip", "7-Zip", "%ProgramFiles%" + Path.DirectorySeparatorChar + "7-Zip");

    private readonly FakeWingetService _winget = new();
    private readonly FakePathStore _path = new();
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void FindsCatalogAndWizardPackages()
    {
        var installer = Create(wizard: c => c == "mytool" ? "Me.MyTool" : null);
        Assert.Equal("7zip.7zip", installer.Find("7z.exe")?.WingetId);
        Assert.Equal("Me.MyTool", installer.Find("mytool")?.WingetId);
        Assert.Null(installer.Find("nothing-here"));
    }

    [Fact]
    public async Task ProgramThatIsNotOnPathIsAddedToSessionAndUserPath()
    {
        var folder = Path.Combine(ProgramFiles, "7-Zip");
        var installer = Create(afterInstall: () => _files.Add(Path.Combine(folder, "7z")));

        var result = await installer.InstallAsync(SevenZip, new ToolInstallOptions(ToolInstallScope.User, AddToPath: true));

        Assert.True(result.Success, result.Message);
        Assert.Equal(Path.Combine(folder, "7z"), result.Executable);
        Assert.Equal([folder], result.AddedToPath);
        Assert.Contains(folder, _path.ProcessPath.Split(Path.PathSeparator));
        Assert.Equal([folder], _path.UserAppends);
        Assert.Equal(WingetScope.User, Assert.Single(_winget.InstallOptions).Scope);
    }

    [Fact]
    public async Task DecliningPathOnlyChangesTheSession()
    {
        var folder = Path.Combine(ProgramFiles, "7-Zip");
        var installer = Create(afterInstall: () => _files.Add(Path.Combine(folder, "7z")));

        var result = await installer.InstallAsync(SevenZip, new ToolInstallOptions(ToolInstallScope.Machine, AddToPath: false));

        Assert.True(result.Success, result.Message);
        Assert.Contains(folder, _path.ProcessPath.Split(Path.PathSeparator));
        Assert.Empty(_path.UserAppends);
        Assert.Equal(WingetScope.Machine, Assert.Single(_winget.InstallOptions).Scope);
    }

    [Fact]
    public async Task InstallerThatUpdatedTheRegistryPathOnlyNeedsTheSessionRefreshed()
    {
        var folder = Path.Combine(Root, "Nmap");
        var installer = Create(afterInstall: () =>
        {
            _path.Persistent.Add(folder);
            _files.Add(Path.Combine(folder, "nmap"));
        });

        var result = await installer.InstallAsync(new ToolPackage("nmap", "Insecure.Nmap", "Nmap"), new ToolInstallOptions());

        Assert.Equal(Path.Combine(folder, "nmap"), result.Executable);
        Assert.Empty(result.AddedToPath!);
        Assert.Empty(_path.UserAppends);
        Assert.Contains(folder, _path.ProcessPath.Split(Path.PathSeparator));
    }

    [Fact]
    public async Task TemporaryInstallsGoToTheSessionFolderAndAreRemoved()
    {
        var temp = Path.Combine(Root, "session");
        var installer = Create(temporaryRoot: temp, afterInstall: () => _files.Add(Path.Combine(temp, "7zip.7zip", "7z")));

        var result = await installer.InstallAsync(SevenZip, new ToolInstallOptions(ToolInstallScope.Temporary, AddToPath: true));

        Assert.True(result.Success, result.Message);
        Assert.Equal(Path.Combine(temp, "7zip.7zip"), Assert.Single(_winget.InstallOptions).Location);
        Assert.Empty(_path.UserAppends);
        Assert.Equal([SevenZip], installer.TemporaryInstalls);
        Assert.Contains(Path.Combine(temp, "7zip.7zip"), _path.ProcessPath.Split(Path.PathSeparator));

        var removed = await installer.RemoveTemporaryAsync();

        Assert.True(removed.Success, removed.Message);
        Assert.Contains("uninstall 7zip.7zip", _winget.Calls);
        Assert.Empty(installer.TemporaryInstalls);
        Assert.DoesNotContain(Path.Combine(temp, "7zip.7zip"), _path.ProcessPath.Split(Path.PathSeparator));
    }

    [Fact]
    public async Task AlreadyInstalledStillFixesPath()
    {
        var folder = Path.Combine(ProgramFiles, "7-Zip");
        _files.Add(Path.Combine(folder, "7z"));
        _winget.Result = call => call == "install 7zip.7zip" ? new WingetOperationResult(false, "No applicable upgrade found.", unchecked((int)0x8A15002B)) : null;
        var installer = Create();

        var result = await installer.InstallAsync(SevenZip, new ToolInstallOptions());

        Assert.True(result.Success, result.Message);
        Assert.StartsWith("7-Zip was already installed", result.Message, StringComparison.Ordinal);
        Assert.Equal([folder], _path.UserAppends);
    }

    [Fact]
    public async Task WingetFailureIsReported()
    {
        _winget.Result = _ => new WingetOperationResult(false, "install 7zip.7zip: No package found matching the id.", 1);
        var installer = Create();

        var result = await installer.InstallAsync(SevenZip, new ToolInstallOptions());

        Assert.False(result.Success);
        Assert.Contains("No package found", result.Message, StringComparison.Ordinal);
        Assert.Empty(installer.TemporaryInstalls);
    }

    private WingetToolInstaller Create(Func<string, string?>? wizard = null, Action? afterInstall = null, string? temporaryRoot = null)
    {
        if (afterInstall is not null)
        {
            var previous = _winget.Result;
            _winget.Result = call =>
            {
                if (call.StartsWith("install ", StringComparison.Ordinal))
                {
                    afterInstall();
                }

                return previous?.Invoke(call);
            };
        }

        return new WingetToolInstaller(
            () => _winget,
            wizard ?? (_ => null),
            _path,
            temporaryRoot ?? Path.Combine(Root, "temp"),
            _files.Contains,
            dir => _files.Any(f => f.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)),
            (dir, name, _) => _files.Where(f => f.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(f) == name));
    }

    private sealed class FakePathStore : IPathStore
    {
        public string ProcessPath { get; set; } = string.Join(Path.PathSeparator, Path.Combine(Root, "bin"));

        public List<string> Persistent { get; } = [];

        public List<string> UserAppends { get; } = [];

        public IReadOnlyList<string> PersistentEntries() => [.. Persistent, .. UserAppends];

        public void AppendToUserPath(string directory) => UserAppends.Add(directory);

        public string Expand(string text) => text.Replace("%ProgramFiles%", ProgramFiles, StringComparison.OrdinalIgnoreCase)
            .Replace("%ProgramFiles(x86)%", ProgramFiles, StringComparison.OrdinalIgnoreCase)
            .Replace("%LOCALAPPDATA%", Path.Combine(Root, "local"), StringComparison.OrdinalIgnoreCase);
    }
}
