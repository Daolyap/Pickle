using Pickle.Abstractions.Services;
using Pickle.Admin.Packages;
using Pickle.Admin.Privilege;
using Pickle.Testing.Fakes;

namespace Pickle.Admin.Tests.Packages;

public class PackageManagerTests
{
    [Fact]
    public void AptParsesInstalledUpgradableAndSearch()
    {
        var installed = AptPackageManager.ParseInstalled("openssl\t3.0.2-0ubuntu1.15\tii \tSecure Sockets Layer toolkit\nghost\t1.0\trc \tremoved but configured\nbash\t5.1-6\tii \tGNU Bourne Again SHell\n");
        var upgrades = AptPackageManager.ParseUpgradable("Listing...\nopenssl/jammy-updates,jammy-security 3.0.2-0ubuntu1.18 amd64 [upgradable from: 3.0.2-0ubuntu1.15]\n");
        var search = AptPackageManager.ParseSearch("ripgrep - Recursively searches directories for a regex pattern\nlibripgrep-dev - dev files\n");

        Assert.Equal(["openssl", "bash"], installed.Select(p => p.Name));
        Assert.Equal("Secure Sockets Layer toolkit", installed[0].Description);
        var openssl = Assert.Single(upgrades);
        Assert.Equal(("openssl", "3.0.2-0ubuntu1.15", "3.0.2-0ubuntu1.18", "jammy-updates,jammy-security"), (openssl.Name, openssl.Version, openssl.NewVersion, openssl.Repository));
        Assert.Equal(["ripgrep", "libripgrep-dev"], search.Select(p => p.Name));
        Assert.All(search, p => Assert.False(p.Installed));
    }

    [Fact]
    public void DnfParsesCheckUpdateAndSearch()
    {
        var updates = DnfPackageManager.ParseCheckUpdate("\nkernel-core.x86_64          6.9.5-200.fc40     updates\nvim-minimal.x86_64          2:9.1.393-1.fc40   updates\nObsoleting Packages\nold.x86_64 1 repo\n");
        var search = DnfPackageManager.ParseSearch("=== Name Matched: ripgrep ===\nripgrep.x86_64 : Line oriented search tool\n=== Summary Matched: ripgrep ===\nfzf.x86_64 : Fuzzy finder\n");

        Assert.Equal(["kernel-core", "vim-minimal"], updates.Select(p => p.Name));
        Assert.Equal("2:9.1.393-1.fc40", updates[1].NewVersion);
        Assert.Equal(["ripgrep", "fzf"], search.Select(p => p.Name));
        Assert.Equal("Line oriented search tool", search[0].Description);
    }

    [Fact]
    public void RpmListSkipsGpgKeys()
    {
        var installed = RpmInstalled.Parse("bash\t5.2.26-3.fc40\tThe GNU Bourne Again shell\ngpg-pubkey\t105ef\tgpg(Fedora)\n");

        Assert.Equal(["bash"], installed.Select(p => p.Name));
    }

    [Fact]
    public void PacmanParsesInstalledUpgradesAndSearch()
    {
        var installed = PacmanPackageManager.ParseInstalled("linux 6.9.1.arch1-1\nvim 9.1.0-1\n");
        var upgrades = PacmanPackageManager.ParseUpgrades("linux 6.9.1.arch1-1 -> 6.9.2.arch1-1\n");
        var search = PacmanPackageManager.ParseSearch("extra/vim 9.1.0-1 [installed]\n    Vi Improved, a highly configurable text editor\nextra/vim-runtime 9.1.0-1\n    Runtime for vim\n");

        Assert.Equal(["linux", "vim"], installed.Select(p => p.Name));
        Assert.Equal(("linux", "6.9.2.arch1-1"), (upgrades[0].Name, upgrades[0].NewVersion));
        Assert.Equal(["vim", "vim-runtime"], search.Select(p => p.Name));
        Assert.True(search[0].Installed);
        Assert.False(search[1].Installed);
        Assert.Equal("Runtime for vim", search[1].Description);
        Assert.Equal("extra", search[0].Repository);
    }

    [Fact]
    public void ZypperParsesTables()
    {
        var updates = ZypperPackageManager.ParseUpdates("S | Repository | Name | Current Version | Available Version | Arch\n--+------+----+----+----+---\nv | Update | curl | 8.0.1-1 | 8.0.2-1 | x86_64\n");
        var search = ZypperPackageManager.ParseSearch("S | Name | Summary | Type\n--+------+-------+-----\ni | git | Distributed version control | package\n  | gitk | Git GUI | package\n");

        Assert.Equal(("curl", "8.0.2-1"), (updates[0].Name, updates[0].NewVersion));
        Assert.Equal(["git", "gitk"], search.Select(p => p.Name));
        Assert.Equal([true, false], search.Select(p => p.Installed));
    }

    [Fact]
    public void BrewParsesListAndOutdated()
    {
        var installed = BrewPackageManager.ParseInstalled("git 2.45.1\nnode 21.0.0 22.1.0\n");
        var outdated = BrewPackageManager.ParseOutdated("node (21.0.0) < 22.1.0\nwget (1.21.3, 1.21.4) < 1.24.5\n");

        Assert.Equal([("git", "2.45.1"), ("node", "22.1.0")], installed.Select(p => (p.Name, p.Version)));
        Assert.Equal(["node"], outdated.Select(p => p.Name));
    }

    [Fact]
    public void NamesAreValidatedBeforeAnyCommandIsBuilt()
    {
        var runner = new FakeProgramRunner().On("apt-get", "install", string.Empty).On("dpkg-query", "-W", string.Empty);
        var apt = new AptPackageManager(runner, new UnixPrivilegeService(runner, () => false, _ => null));

        Assert.Equal("sudo apt-get install ripgrep fzf", apt.ShellCommand(PackageAction.Install, ["ripgrep", "fzf"]));
        Assert.Equal("sudo apt-get install --only-upgrade openssl", apt.ShellCommand(PackageAction.Upgrade, ["openssl"]));
        Assert.Equal("sudo apt-get update", apt.ShellCommand(PackageAction.RefreshIndex, []));
        Assert.Throws<ArgumentException>(() => apt.ShellCommand(PackageAction.Install, ["--allow-unauthenticated"]));
        Assert.Throws<ArgumentException>(() => apt.ShellCommand(PackageAction.Install, ["a; rm -rf /"]));
        Assert.Throws<ArgumentException>(() => apt.ShellCommand(PackageAction.Remove, []));
    }

    [Fact]
    public async Task InstallRunsThroughThePrivilegeServiceAndABrewInstallDoesNot()
    {
        var runner = new FakeProgramRunner().On("apt-get", "install", string.Empty).On("sudo", "-n", string.Empty).On("brew", "install", string.Empty);
        var privilege = new UnixPrivilegeService(runner, () => false, _ => null);

        var apt = await new AptPackageManager(runner, privilege).RunAsync(PackageAction.Install, ["ripgrep"]);
        var brew = await new BrewPackageManager(runner, privilege).RunAsync(PackageAction.Install, ["ripgrep"]);

        Assert.True(apt.Success && brew.Success);
        Assert.Equal(["-n /usr/bin/apt-get install ripgrep"], runner.CommandLines("sudo"));
        Assert.Equal(["install ripgrep"], runner.CommandLines("brew"));
    }

    [Fact]
    public async Task SearchMarksWhatIsAlreadyInstalled()
    {
        var runner = new FakeProgramRunner()
            .On("apt-cache", "search", "ripgrep - search tool\nfzf - fuzzy finder\n")
            .On("dpkg-query", "-W", "fzf\t0.44\tii \tfinder\n");
        var apt = new AptPackageManager(runner, new UnixPrivilegeService(runner, () => false, _ => null));

        var found = await apt.SearchAsync("r");

        Assert.Equal([("ripgrep", false), ("fzf", true)], found.Select(p => (p.Name, p.Installed)));
        Assert.Equal(["search --names-only -- r"], runner.CommandLines("apt-cache"));
    }

    [Fact]
    public void DetectionPrefersTheDistributionManagerAndFallsBackToTheFirstCandidate()
    {
        var withPacman = new FakeProgramRunner().On("pacman", "-Q", string.Empty);
        var none = new FakeProgramRunner();
        var privilege = new UnixPrivilegeService(none, () => false, _ => null);

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("pacman", PackageManagers.Detect(withPacman, privilege).Name);
            Assert.False(PackageManagers.Detect(none, privilege).IsSupported);
        }
    }
}
