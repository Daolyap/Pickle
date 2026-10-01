using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Tests;

namespace Pickle.Modules.Tests;

/// <summary>Starts a runtime with one optional module on and a scripted program runner instead of the real one.</summary>
internal static class ModuleTestSupport
{
    public static TestPickle Start(string module, FakeProgramRunner runner)
    {
        var t = TestPickle.Create(start: true, plugins: [new Pickle.Tui.TuiPlugin()], modules: OptionalModules.All, machineModules: [module]);
        t.Runtime.Services.Add<IProgramRunner>(runner);
        return t;
    }

    public static (TestPickle T, Pickle.Tui.PanelHost Host) StartPanels(string module, FakeProgramRunner runner, UiScript script)
    {
        var (t, host) = TuiHarness.Start(script, modules: OptionalModules.All, machineModules: [module]);
        t.Runtime.Services.Add<IProgramRunner>(runner);
        return (t, host);
    }
}
