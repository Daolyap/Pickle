using System.Text.Json;
using System.Xml.Linq;
using Pickle.Abstractions.Services;
using Pickle.Core.Commands;
using Pickle.Core.Modules;

namespace Pickle.Modules.Tests;

/// <summary>packaging/modules.json drives the MSI features and the RPM sub-packages: it must match the modules in code.</summary>
public class PackagingTests
{
    private sealed record Entry(string Id, string Name, string Description, string[] Platforms, string[] RpmRecommends);

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Pickle.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (Pickle.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static IReadOnlyList<Entry> Entries()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "packaging", "modules.json")));
        return [.. document.RootElement.GetProperty("modules").EnumerateArray().Select(e => new Entry(
            e.GetProperty("id").GetString()!,
            e.GetProperty("name").GetString()!,
            e.GetProperty("description").GetString()!,
            [.. e.GetProperty("platforms").EnumerateArray().Select(p => p.GetString()!)],
            [.. e.GetProperty("rpmRecommends").EnumerateArray().Select(p => p.GetString()!)]))];
    }

    private static string[] PlatformNames(ModulePlatforms platforms) =>
    [
        .. new[] { (ModulePlatforms.Windows, "windows"), (ModulePlatforms.Linux, "linux"), (ModulePlatforms.MacOS, "macos") }.Where(p => platforms.HasFlag(p.Item1)).Select(p => p.Item2),
    ];

    [Fact]
    public void EveryVisibleModuleIsListedForTheInstallersAndNothingElse()
    {
        var visible = OptionalModules.All.Where(m => !m.Hidden).OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        var listed = Entries().OrderBy(e => e.Id, StringComparer.Ordinal).ToList();

        Assert.Equal(visible.Select(m => m.Id), listed.Select(e => e.Id));
        foreach (var (module, entry) in visible.Zip(listed))
        {
            Assert.Equal(module.Name, entry.Name);
            Assert.Equal(module.Description, entry.Description);
            Assert.Equal(PlatformNames(module.Platforms), entry.Platforms.Order(StringComparer.Ordinal).Order(new PlatformOrder()));
        }
    }

    private sealed class PlatformOrder : IComparer<string>
    {
        private static readonly string[] Order = ["windows", "linux", "macos"];

        public int Compare(string? x, string? y) => Array.IndexOf(Order, x).CompareTo(Array.IndexOf(Order, y));
    }

    [Fact]
    public void TheHiddenExampleModuleIsNeverOfferedByAnInstaller() =>
        Assert.DoesNotContain(Entries(), e => OptionalModules.All.Any(m => m.Hidden && m.Id == e.Id));

    [Fact]
    public void ModuleIdsAreValidForTheMarkersAndTheRegistry()
    {
        Assert.All(Entries(), e => Assert.True(MachineModules.IsValidId(e.Id), e.Id));
        Assert.Equal(Entries().Count, Entries().Select(e => e.Id.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void AMarkerFileNamedByAnIdTurnsThatModuleOn()
    {
        var directory = Directory.CreateTempSubdirectory("pickle-markers").FullName;
        try
        {
            var markers = Path.Combine(directory, MachineModules.MarkerFolder);
            Directory.CreateDirectory(markers);
            foreach (var entry in Entries())
            {
                File.WriteAllText(Path.Combine(markers, entry.Id), string.Empty);
            }

            var read = MachineModules.Read(new MachineModuleSources(null, directory, null, () => []));

            Assert.Equal(Entries().Select(e => e.Id).Order(StringComparer.Ordinal), read.Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // "python3" can be the Microsoft Store stub on Windows (it prints a hint and fails): use the first candidate that really is Python 3.
    private static async Task<string?> FindPython(ProgramRunnerService runner)
    {
        foreach (var name in new[] { "python3", "python", "py" })
        {
            if (runner.Find(name) is { } path && await runner.RunAsync(path, ["--version"], new ProgramRunOptions { Timeout = TimeSpan.FromSeconds(10) }) is { Success: true } version
                && (version.StdOut + version.StdErr).StartsWith("Python 3", StringComparison.Ordinal))
            {
                return path;
            }
        }

        return null;
    }

    private static async Task<string?> Generate(params string[] arguments)
    {
        var runner = new ProgramRunnerService();
        if (await FindPython(runner) is not { } python)
        {
            return null;
        }

        var result = await runner.RunAsync(python, [Path.Combine(Root(), "packaging", "modules.py"), .. arguments], new ProgramRunOptions { Timeout = TimeSpan.FromSeconds(30) });
        Assert.True(result.Success, result.StdErr);
        return result.StdOut.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMsiFragmentHasAnOffByDefaultFeatureThatWritesTheRegistryValueForEachModule()
    {
        var xml = await Generate("wix");
        Assert.SkipWhen(xml is null, "Python is not installed");
        XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";
        var document = XDocument.Parse(xml!);

        var features = document.Descendants(wix + "Feature").Where(f => f.Attribute("Id")!.Value.StartsWith("Module_", StringComparison.Ordinal)).ToList();
        var ids = Entries().Select(e => e.Id).ToList();

        Assert.Equal(ids.Select(i => "Module_" + i.Replace('-', '_')), features.Select(f => f.Attribute("Id")!.Value));
        Assert.All(features, f => Assert.Equal("2", f.Attribute("Level")!.Value));
        Assert.Equal(ids, document.Descendants(wix + "RegistryValue").Where(v => v.Attribute("Key")!.Value == @"Software\Pickle\Modules" && v.Attribute("KeyPath") is not null).Select(v => v.Attribute("Name")!.Value));
        Assert.All(document.Descendants(wix + "RegistryValue").Where(v => v.Attribute("Type")?.Value == "integer"), v => Assert.Equal("1", v.Attribute("Value")!.Value));
        Assert.Equal("ModulesFeature", document.Descendants(wix + "Feature").First().Attribute("Id")!.Value);
        // A search property must be public: all upper case.
        Assert.All(document.Descendants(wix + "Property"), p => Assert.Equal(p.Attribute("Id")!.Value.ToUpperInvariant(), p.Attribute("Id")!.Value));
        Assert.All(features, f => Assert.Equal(f.Descendants(wix + "Level").Single().Attribute("Condition")!.Value, "PREVMODULE_" + f.Attribute("Id")!.Value["Module_".Length..].ToUpperInvariant()));
    }

    [Fact]
    public async Task TheRpmSpecGetsASubPackageWithAMarkerFilePerModuleAndAMetaPackage()
    {
        var spec = await Generate("rpm-spec", Path.Combine(Root(), "packaging", "rpm", "pickle.spec"));
        Assert.SkipWhen(spec is null, "Python is not installed");

        Assert.DoesNotContain("@@", spec, StringComparison.Ordinal);
        foreach (var entry in Entries().Where(e => e.Platforms.Contains("linux")))
        {
            Assert.Contains($"%package -n pickle-module-{entry.Id}\n", spec, StringComparison.Ordinal);
            Assert.Contains($"%files -n pickle-module-{entry.Id}\n/etc/pickle/modules.d/{entry.Id}\n", spec, StringComparison.Ordinal);
            Assert.Contains($"install -Dm0644 /dev/null %{{buildroot}}/etc/pickle/modules.d/{entry.Id}\n", spec, StringComparison.Ordinal);
            Assert.Contains($"Requires:       pickle-module-{entry.Id} = %{{version}}-%{{release}}", spec, StringComparison.Ordinal);
            foreach (var package in entry.RpmRecommends)
            {
                Assert.Contains($"Recommends:     {package}\n", spec, StringComparison.Ordinal);
            }
        }

        Assert.Contains("%package -n pickle-modules-all", spec, StringComparison.Ordinal);
        Assert.Contains("%dir /etc/pickle/modules.d", spec, StringComparison.Ordinal);
        Assert.Equal(1, spec!.Split("%files\n").Length - 1);
    }

    [Fact]
    public async Task TheGeneratorValidatesTheJson()
    {
        var checkedModules = await Generate("check");
        Assert.SkipWhen(checkedModules is null, "Python is not installed");

        Assert.Equal($"{Entries().Count} modules", checkedModules!.Trim());
    }
}
