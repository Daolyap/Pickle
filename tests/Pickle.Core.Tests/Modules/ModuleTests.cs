using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.Modules;
using Pickle.Testing;

namespace Pickle.Core.Tests.Modules;

public class ModuleTests
{
    private sealed class FakePlugin(string id, Action<IPickleContext>? initialize = null) : IPicklePlugin
    {
        public string Id => "pickle." + id;
        public string DisplayName => id;
        public string Description => id;
        public void Initialize(IPickleContext context) => initialize?.Invoke(context);
    }

    private sealed class HelloCommand(string name) : IPickleCommand
    {
        public string Name => name;
        public string Description => name;
        public string Usage => "pk " + name;

        public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            context.WriteObject("hi from " + name);
            return ValueTask.FromResult(0);
        }
    }

    private static ModuleDescriptor Module(string id, List<string>? created = null, ModulePlatforms platforms = ModulePlatforms.All, bool fails = false) => new()
    {
        Id = id,
        Name = id.ToUpperInvariant(),
        Description = "Module " + id,
        Platforms = platforms,
        Create = () =>
        {
            created?.Add(id);
            return new FakePlugin(id, ctx =>
            {
                if (fails)
                {
                    throw new InvalidOperationException("boom");
                }

                ctx.Commands.Register(new HelloCommand(id));
            });
        },
    };

    [Fact]
    public void NothingIsLoadedUntilSelected()
    {
        var created = new List<string>();
        using var t = TestPickle.Create(start: true, modules: [Module("alpha", created), Module("beta", created)]);

        Assert.Empty(created);
        Assert.All(t.Runtime.ModuleCatalog.Status(), s => Assert.Equal(ModuleState.Off, s.State));
        Assert.Null(t.Runtime.CommandRegistry.Get("alpha"));
    }

    [Fact]
    public void InstallerSelectionLoadsAtStartup()
    {
        var created = new List<string>();
        using var t = TestPickle.Create(start: true, modules: [Module("alpha", created), Module("beta", created)], machineModules: ["beta"]);

        Assert.Equal(["beta"], created);
        var beta = t.Runtime.ModuleCatalog.Find("beta")!;
        Assert.Equal(ModuleState.Loaded, beta.State);
        Assert.Equal("installer", beta.Source);
        Assert.NotNull(t.Runtime.CommandRegistry.Get("beta"));
        Assert.Contains(t.Runtime.PluginManager.Plugins, p => p.Kind == "module" && p.Id == "beta" && p.Contributions.Contains("command: beta"));
    }

    [Fact]
    public void EnablingLoadsNowAndPersistsDisablingStaysLoadedUntilRestart()
    {
        using var t = TestPickle.Create(start: true, modules: [Module("alpha")]);
        var catalog = t.Runtime.ModuleCatalog;

        var on = catalog.Enable("alpha");
        Assert.Equal(ModuleState.Loaded, on.State);
        Assert.Contains("alpha", t.Runtime.Config.Current.Modules.Enabled);
        Assert.NotNull(t.Runtime.CommandRegistry.Get("alpha"));
        Assert.Equal(1, t.Runtime.PluginManager.Plugins.Count(p => p.Kind == "module"));

        catalog.Enable("alpha");
        Assert.Equal(1, t.Runtime.PluginManager.Plugins.Count(p => p.Kind == "module"));

        var off = catalog.Disable("alpha");
        Assert.False(off.Enabled);
        Assert.Equal(ModuleState.Loaded, off.State);
        Assert.Empty(t.Runtime.Config.Current.Modules.Enabled);
        Assert.Equal("on until restart", ModuleCommand.Label(off));
    }

    [Fact]
    public void UserCanTurnOffWhatTheInstallerSelected()
    {
        var created = new List<string>();
        using var t = TestPickle.Create(start: true, modules: [Module("alpha", created)], machineModules: ["alpha"]);
        t.Runtime.ModuleCatalog.Disable("alpha");
        Assert.Contains("alpha", t.Runtime.Config.Current.Modules.Disabled);
        t.Runtime.Config.Reload();

        using var again = TestPickle.Create(start: true, modules: [Module("alpha", created)], machineModules: ["alpha"], configure: c => c.Modules.Disabled.Add("alpha"));
        Assert.Equal(ModuleState.Off, again.Runtime.ModuleCatalog.Find("alpha")!.State);
        Assert.Single(created);
    }

    [Fact]
    public void AFailingModuleIsMarkedFailedAndOthersStillLoad()
    {
        using var t = TestPickle.Create(start: true, modules: [Module("bad", fails: true), Module("good")], machineModules: ["bad", "good"]);

        var bad = t.Runtime.ModuleCatalog.Find("bad")!;
        Assert.Equal(ModuleState.Failed, bad.State);
        Assert.Equal("boom", bad.Error);
        Assert.Equal(ModuleState.Loaded, t.Runtime.ModuleCatalog.Find("good")!.State);
    }

    [Fact]
    public void ModulesForOtherPlatformsAreNeverCreated()
    {
        var other = OperatingSystem.IsWindows() ? ModulePlatforms.Linux : ModulePlatforms.Windows;
        var created = new List<string>();
        using var t = TestPickle.Create(start: true, modules: [Module("elsewhere", created, other)], machineModules: ["elsewhere"]);

        Assert.Empty(created);
        Assert.Equal(ModuleState.Unsupported, t.Runtime.ModuleCatalog.Find("elsewhere")!.State);
    }

    [Fact]
    public void SelectReplacesTheUsersChoice()
    {
        using var t = TestPickle.Create(start: true, modules: [Module("a"), Module("b"), Module("c")], configure: c => c.Modules.Enabled.AddRange(["a", "b"]));
        t.Runtime.ModuleCatalog.Select(new HashSet<string> { "b", "c" });

        Assert.Equal(["b", "c"], t.Runtime.Config.Current.Modules.Enabled.Order());
    }

    [Fact]
    public void MachineSelectionReadsEnvironmentMarkersAndRegistry()
    {
        var root = Path.Combine(Path.GetTempPath(), "pickle-tests", Guid.NewGuid().ToString("N")[..10]);
        var exe = Path.Combine(root, "exe");
        var etc = Path.Combine(root, "etc");
        Directory.CreateDirectory(Path.Combine(exe, MachineModules.MarkerFolder));
        Directory.CreateDirectory(Path.Combine(etc, MachineModules.MarkerFolder));
        File.WriteAllText(Path.Combine(exe, MachineModules.MarkerFolder, "docker"), string.Empty);
        File.WriteAllText(Path.Combine(etc, MachineModules.MarkerFolder, "nmap.conf"), string.Empty);
        File.WriteAllText(Path.Combine(etc, MachineModules.MarkerFolder, "../bad"), string.Empty);
        try
        {
            var ids = MachineModules.Read(new MachineModuleSources(exe, etc, "Vault; kubernetes,../evil", () => ["github", "not valid!"]));

            Assert.Equal(["docker", "github", "kubernetes", "nmap", "vault"], ids.Order());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ThePickerTogglesAndAccepts()
    {
        using var t = TestPickle.Create(start: true, modules: [Module("a"), Module("b"), Module("c")], configure: c => c.Modules.Enabled.Add("a"));
        t.Terminal.Press("Down", "Spacebar", "Down", "Spacebar", "Up", "Spacebar", "Enter");

        var picked = ModulePicker.Run(t.Terminal, t.Runtime.Themes.Current.Ui, t.Runtime.ModuleCatalog.Status());

        Assert.NotNull(picked);
        Assert.Equal(["a", "c"], picked!.Order());
        Assert.Contains("[x]", t.Terminal.GetScreenText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePickerCancelsWithEscapeAndNeedsAnInteractiveTerminal()
    {
        using var t = TestPickle.Create(start: true, modules: [Module("a")]);
        t.Terminal.Press("Spacebar", "Escape");
        Assert.Null(ModulePicker.Run(t.Terminal, t.Runtime.Themes.Current.Ui, t.Runtime.ModuleCatalog.Status()));

        t.Terminal.IsInteractive = false;
        Assert.Null(ModulePicker.Run(t.Terminal, t.Runtime.Themes.Current.Ui, t.Runtime.ModuleCatalog.Status()));
    }

    [Fact]
    public void PkModuleListsEnablesAndRejectsUnknownIds()
    {
        using var t = TestPickle.Create(start: true, modules: [Module("alpha"), Module("beta")]);

        var list = string.Join('\n', t.Run("pk module list | ForEach-Object { '{0}={1}' -f $_.Id, $_.State }"));
        Assert.Contains("alpha=off", list, StringComparison.Ordinal);

        t.Run("pk module enable alpha");
        Assert.Contains("alpha=on", string.Join('\n', t.Run("pk module list | ForEach-Object { '{0}={1}' -f $_.Id, $_.State }")), StringComparison.Ordinal);
        Assert.Equal("hi from alpha", t.Run("pk alpha")[0]);

        var exit = t.Run("pk module enable nope; $LASTEXITCODE");
        Assert.Contains("1", exit);
    }

    [Fact]
    public void TheSetupOfferListsTheModulesAndAppliesThePick()
    {
        using var t = TestPickle.Create(start: true, modules: [Module("alpha"), Module("beta")]);
        var offer = t.Runtime.FirstRun.All.Single(o => o.Id == "modules");

        Assert.True(offer.IsRelevant());
        t.Terminal.Press("Spacebar", "Enter");
        var message = offer.Accept();

        Assert.Contains("alpha", message, StringComparison.Ordinal);
        Assert.Equal(["alpha"], t.Runtime.Config.Current.Modules.Enabled);
    }
}
