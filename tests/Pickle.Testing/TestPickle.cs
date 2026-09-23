using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Core;
using Pickle.Core.Logging;

namespace Pickle.Testing;

/// <summary>
/// An isolated Pickle runtime for tests: temp config/data dirs, a <see cref="VirtualTerminal"/>, optional config
/// tweaks, and (when <c>start</c> is true) an open PowerShell runspace. Always dispose.
/// <code>
/// using var t = TestPickle.Create(start: true);
/// t.Terminal.Type("Get-Date").Press("Enter");
/// </code>
/// </summary>
public sealed class TestPickle : IDisposable
{
    private TestPickle(string home, VirtualTerminal terminal, PickleRuntime runtime)
    {
        Home = home;
        Terminal = terminal;
        Runtime = runtime;
    }

    public string Home { get; }

    public VirtualTerminal Terminal { get; }

    public PickleRuntime Runtime { get; }

    public PicklePaths Paths => Runtime.Paths;

    public static TestPickle Create(
        int width = 80,
        int height = 24,
        Action<PickleConfig>? configure = null,
        bool start = false,
        IReadOnlyList<IPicklePlugin>? plugins = null)
    {
        var home = Path.Combine(Path.GetTempPath(), "pickle-tests", Guid.NewGuid().ToString("N")[..12]);
        var paths = new PicklePaths(Path.Combine(home, "config"), Path.Combine(home, "data"));
        paths.EnsureCreated();

        var config = new PickleConfig();
        config.Shell.ShowStartupBanner = false;
        configure?.Invoke(config);
        File.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(config, PickleJson.Options));

        var terminal = new VirtualTerminal(width, height);
        var options = new PickleOptions { NoProfile = true, NoLogo = true };
        var runtime = new PickleRuntime(options, terminal, paths, new FileLogger(paths.LogDir, PickleLogLevel.Debug));
        var test = new TestPickle(home, terminal, runtime);
        if (start)
        {
            runtime.InitializeComponents();
            runtime.Start(plugins ?? []);
        }

        return test;
    }

    /// <summary>Runs a script in the main runspace and returns its output as strings.</summary>
    public IReadOnlyList<string> Run(string script)
    {
        var result = Runtime.Shell.InvokeAsync(script).GetAwaiter().GetResult();
        if (result.HadErrors)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, result.Errors));
        }

        return [.. result.Output.Select(o => o?.ToString() ?? string.Empty)];
    }

    public void Dispose()
    {
        Runtime.Dispose();
        try
        {
            Directory.Delete(Home, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
