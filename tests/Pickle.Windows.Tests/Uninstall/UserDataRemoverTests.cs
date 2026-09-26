using Pickle.Abstractions;
using Pickle.Windows.Terminal;
using Pickle.Windows.Uninstall;

namespace Pickle.Windows.Tests.Uninstall;

public sealed class UserDataRemoverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pickle-uninstall").FullName;

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

    [Fact]
    public void RemovesConfigDataAndTheTerminalProfile()
    {
        var paths = new PicklePaths(Path.Combine(_root, "config"), Path.Combine(_root, "data"));
        paths.EnsureCreated();
        File.WriteAllText(paths.HistoryFile, "{}\n");
        var locations = WindowsTerminalLocations.FromLocalAppData(Path.Combine(_root, "local"));
        new WindowsTerminalManager(locations).Install(@"C:\Pickle\pickle.exe", new TerminalSettings(), new TerminalPalette());
        var output = new StringWriter();

        Assert.Equal([paths.ConfigDir, paths.DataDir, locations.FragmentDirectory], UserDataRemover.Targets(paths, locations));
        Assert.Equal(0, UserDataRemover.Remove(paths, locations, output));

        Assert.False(Directory.Exists(paths.ConfigDir));
        Assert.False(Directory.Exists(paths.DataDir));
        Assert.False(File.Exists(locations.FragmentFile));
        Assert.Contains("Deleted " + paths.ConfigDir, output.ToString(), StringComparison.Ordinal);
        Assert.Empty(UserDataRemover.Targets(paths, locations));
    }

    [Fact]
    public void NeverDeletesRootsOrTheUsersOwnFolders()
    {
        Assert.False(UserDataRemover.IsSafeToDelete(Path.GetPathRoot(_root)!));
        Assert.False(UserDataRemover.IsSafeToDelete(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.True(UserDataRemover.IsSafeToDelete(Path.Combine(_root, "config")));
    }
}
