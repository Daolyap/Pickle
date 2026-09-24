using System.Xml.Linq;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Windows.Elevation;
using Pickle.Windows.Sandbox;

namespace Pickle.Windows.Tests.Sandbox;

public sealed class SandboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pickle-sandbox-tests", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void WsbHasOnlyTheSettingsThatAreSet()
    {
        var config = new SandboxConfig
        {
            Networking = SandboxSwitch.Disable,
            ProtectedClient = SandboxSwitch.Enable,
            MemoryInMB = 4096,
            MappedFolders = [new SandboxMappedFolder { HostFolder = @"C:\Users\me\Downloads" }, new SandboxMappedFolder { HostFolder = @"D:\Work", SandboxFolder = @"C:\Work", ReadOnly = false }],
            LogonCommand = "explorer.exe C:\\Work",
        };

        var xml = XElement.Parse(WsbBuilder.BuildXml(config, @"C:\setup", null));

        Assert.Equal(["Networking", "MappedFolders", "LogonCommand", "ProtectedClient", "MemoryInMB"], xml.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("Disable", xml.Element("Networking")!.Value);
        var folders = xml.Element("MappedFolders")!.Elements().ToList();
        Assert.Equal(2, folders.Count);
        Assert.Null(folders[0].Element("SandboxFolder"));
        Assert.Equal("true", folders[0].Element("ReadOnly")!.Value);
        Assert.Equal(("C:\\Work", "false"), (folders[1].Element("SandboxFolder")!.Value, folders[1].Element("ReadOnly")!.Value));
        Assert.Equal("explorer.exe C:\\Work", xml.Element("LogonCommand")!.Element("Command")!.Value);
    }

    [Fact]
    public void CustomisationsShareTheSetupFolderAndRunItAtLogon()
    {
        var config = new SandboxConfig { DarkMode = true, StartPickle = true, WingetPackages = ["Git.Git"], LogonCommand = "notepad.exe" };

        var xml = XElement.Parse(WsbBuilder.BuildXml(config, @"C:\data\setup", @"C:\Program Files\Pickle"));

        var folders = xml.Element("MappedFolders")!.Elements().Select(f => (f.Element("HostFolder")!.Value, f.Element("SandboxFolder")!.Value, f.Element("ReadOnly")!.Value)).ToList();
        Assert.Equal([(@"C:\data\setup", WsbBuilder.SetupMount, "true"), (@"C:\Program Files\Pickle", WsbBuilder.PickleMount, "true")], folders);
        Assert.Equal($@"powershell.exe -NoProfile -ExecutionPolicy Bypass -File ""{WsbBuilder.SetupMount}\setup.ps1""", xml.Element("LogonCommand")!.Element("Command")!.Value);

        var script = WsbBuilder.BuildSetupScript(config);
        Assert.Contains("AppsUseLightTheme -Value 0", script, StringComparison.Ordinal);
        Assert.Contains("Repair-WinGetPackageManager", script, StringComparison.Ordinal);
        Assert.Contains("install --id 'Git.Git' --exact", script, StringComparison.Ordinal);
        Assert.Contains($"Start-Process '{WsbBuilder.PickleMount}\\pickle.exe'", script, StringComparison.Ordinal);
        Assert.Contains("Start-Process cmd.exe -ArgumentList '/c', 'notepad.exe'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupScriptQuotesUserValues()
    {
        var script = WsbBuilder.BuildSetupScript(new SandboxConfig { StartUrl = "https://x.test/?q=it's", MappedFolders = [new SandboxMappedFolder { HostFolder = @"C:\O'Brien" }], OpenMappedFolder = true });

        Assert.Contains("-ArgumentList 'https://x.test/?q=it''s'", script, StringComparison.Ordinal);
        Assert.Contains($"-ArgumentList '{WsbBuilder.SandboxDesktop}\\O''Brien'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationCatchesBrokenSetups()
    {
        var errors = WsbBuilder.Validate(new SandboxConfig
        {
            Name = "bad/name",
            Networking = SandboxSwitch.Disable,
            WingetPackages = ["Git.Git", "not an id"],
            MappedFolders = [new SandboxMappedFolder { HostFolder = "relative" }],
            StartUrl = "nope",
            MemoryInMB = 512,
        });

        Assert.Equal(6, errors.Count);
        Assert.Contains(errors, e => e.Contains("needs networking", StringComparison.Ordinal));
        Assert.Empty(WsbBuilder.Validate(new SandboxConfig { Name = "Fine one", Networking = SandboxSwitch.Enable, WingetPackages = ["Git.Git"] }));
    }

    [Fact]
    public void PresetsAreValidAndDistinct()
    {
        var presets = SandboxPresets.Create(Path.Combine(_root, "Downloads"), Path.Combine(_root, "src"));
        Assert.Equal(presets.Count, presets.Select(p => p.Name).Distinct().Count());
        Assert.All(presets, p => Assert.Empty(WsbBuilder.Validate(p)));
    }

    [Fact]
    public async Task ServiceSavesLaunchesAndExports()
    {
        var paths = new PicklePaths(Path.Combine(_root, "config"), Path.Combine(_root, "data"));
        var started = new List<string>();
        var status = new SandboxStatus(true, true, false, null);
        var service = new WindowsSandboxService(paths, () => null, () => _root, () => status, (wsb, _) => { started.Add(wsb); return true; }, () => null, isSupported: true);

        service.Save(new SandboxConfig { Name = "My box", DarkMode = true });
        Assert.Equal("My box", Assert.Single(service.LoadSaved()).Name);
        Assert.Throws<ArgumentException>(() => service.Save(new SandboxConfig { Name = "../evil" }));

        var launched = await service.LaunchAsync(service.LoadSaved()[0]);
        Assert.True(launched.Success, launched.Message);
        var wsb = Assert.Single(started);
        Assert.Equal(Path.Combine(service.RunDirectory, "My box", "My box.wsb"), wsb);
        Assert.True(File.Exists(Path.Combine(service.RunDirectory, "My box", "setup", "setup.ps1")));

        status = new SandboxStatus(true, true, true, null);
        Assert.Contains("already running", (await service.LaunchAsync(new SandboxConfig())).Message, StringComparison.Ordinal);
        status = new SandboxStatus(true, false, false, null);
        Assert.Contains("pk sandbox enable", (await service.LaunchAsync(new SandboxConfig())).Message, StringComparison.Ordinal);

        var exported = service.Export(new SandboxConfig { Name = "Out", ShowFileExtensions = true }, Path.Combine(_root, "out"));
        Assert.True(exported.Success, exported.Message);
        Assert.True(File.Exists(Path.Combine(_root, "out.wsb")));
        Assert.True(File.Exists(Path.Combine(_root, "out.setup", "setup.ps1")));

        Assert.True(service.Delete("My box"));
        Assert.Empty(service.LoadSaved());
    }

    [Fact]
    public async Task EnablingTheFeatureIsOneAllowlistedElevatedOperation()
    {
        var broker = new FakeElevationBroker();
        var paths = new PicklePaths(Path.Combine(_root, "config"), Path.Combine(_root, "data"));
        var service = new WindowsSandboxService(paths, () => broker, () => _root, () => new SandboxStatus(true, false, false, null), (_, _) => true, () => null, isSupported: true);

        var result = await service.EnableFeatureAsync();

        Assert.True(result.Success, result.Message);
        var request = Assert.Single(broker.Requests);
        Assert.Equal((ElevatedOperationKind.EnableWindowsSandbox, 0), (request.Kind, request.Arguments.Count));

        Assert.Equal(ElevatedOperationKind.EnableWindowsSandbox, ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.EnableWindowsSandbox, [])).Kind);
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.EnableWindowsSandbox, ["/Disable-Feature"])));
        var executor = new FakeExecutor();
        await ElevatedOperations.ExecuteAsync(ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.EnableWindowsSandbox, [])), executor, new Progress<string>(), CancellationToken.None);
        Assert.Equal(["enable-sandbox"], executor.Calls);
    }
}
