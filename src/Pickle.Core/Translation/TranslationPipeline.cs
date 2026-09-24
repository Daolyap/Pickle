using System.Collections;
using System.Text;
using Pickle.Abstractions;
using Pickle.Core.Aliases;
using Pickle.Core.Contracts;
using Pickle.Core.Hosting;
using Pickle.Core.Translation.Rewriters;

namespace Pickle.Core.Translation;

/// <summary>
/// Linux-syntax translation: runs the registered <see cref="IInputRewriter"/>s over accepted lines, loads the
/// Pickle.Translate shim module (Windows by default; <see cref="LoadShims"/> forces it elsewhere), defines registry shims,
/// installs the command-not-found handler and provides <c>pk translate</c>.
/// </summary>
public sealed class TranslationPipeline : ITranslationPipeline, IRuntimeComponent, IDisposable
{
    public const string ModuleName = "Pickle.Translate";

    private static readonly Dictionary<string, string> RewriterDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["history"] = "!! → previous command, !$ → its last argument",
        ["devnull"] = ">/dev/null, 2>/dev/null, &>/dev/null → $null redirections",
        ["packages"] = "apt/brew/dnf/pacman/choco install|search|upgrade → winget (Windows)",
        ["export"] = "export VAR=value → $env:VAR, unset VAR, source file → . file",
        ["env-prefix"] = "VAR=value cmd → $env:VAR set only while cmd runs",
        ["sudo"] = "sudo cmd → Windows sudo.exe or an elevated Pickle window (Windows)",
    };

    // Restores the aliases/functions the module displaced. Done here rather than in the module's OnRemove because
    // anything created from module code is owned by the module and removed along with it.
    private const string UnloadScript = """
        param($path, $options)
        $global:PickleTranslateOptions = $options
        $loaded = Get-Module -Name Pickle.Translate
        if ($loaded) {
            $savedAliases = & $loaded { $script:SavedAliases }
            $savedFunctions = & $loaded { $script:SavedFunctions }
            Remove-Module -ModuleInfo $loaded -Force
            foreach ($a in $savedAliases) { Set-Alias -Name $a.Name -Value $a.Definition -Option $a.Options -Scope Global -Force -ErrorAction Ignore }
            foreach ($name in $savedFunctions.Keys) { Set-Item -LiteralPath "Function:\global:$name" -Value $savedFunctions[$name] -Force }
        }
        """;

    private readonly PickleRuntime _runtime;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly object _gate = new();
    private volatile bool _dirty;
    private bool _forceShims;
    private const string NotApplied = "\0";

    // Starts as NotApplied so the first Reconcile always publishes $PickleTranslateOptions (autoload honors it).
    private string? _appliedKey = NotApplied;

    public TranslationPipeline(PickleRuntime runtime)
    {
        _runtime = runtime;
        CommandNotFound = new CommandNotFound(runtime);
    }

    public CommandNotFound CommandNotFound { get; }

    public bool ShimsLoaded => _appliedKey is not null and not NotApplied;

    public void Initialize()
    {
        var isWindows = OperatingSystem.IsWindows();
        var registry = _runtime.TranslationRegistry;
        registry.RegisterRewriter(new HistoryExpansionRewriter());
        registry.RegisterRewriter(new DevNullRewriter());
        registry.RegisterRewriter(new PackageManagerRewriter(isWindows, name => LookupCommand(name) is not null, name => _runtime.CommandRegistry.Get(name) is not null));
        registry.RegisterRewriter(new ExportRewriter(isWindows));
        registry.RegisterRewriter(new EnvPrefixRewriter(isWindows));
        registry.RegisterRewriter(new SudoRewriter(isWindows, LookupCommand, isWindows ? SudoRewriter.FindBuiltInSudo() : null, Environment.ProcessPath ?? "pickle"));
        _runtime.CommandRegistry.Register(new TranslateCommand(this));
    }

    public void OnStarted()
    {
        _runtime.Config.Changed += OnConfigChanged;
        _subscriptions.Add(_runtime.Hooks.Register(HookKind.PostExecute, OnIdle));
        _subscriptions.Add(_runtime.Hooks.Register(HookKind.Prompt, OnIdle));

        Reconcile();
        var shims = BuildShimScript();
        if (shims.Length > 0)
        {
            _runtime.Engine.InvokeSilently(shims);
        }

        _runtime.Engine.InvokeSilently(CommandNotFound.HandlerScript);
    }

    public TranslationOutcome Translate(string input, string cwd)
    {
        var settings = _runtime.Config.Current.Translation;
        if (!settings.Enabled)
        {
            return new TranslationOutcome(input, null);
        }

        var current = input;
        var explanations = new List<string>();
        var context = new RewriteContext(cwd, [.. _runtime.History.Entries.TakeLast(20).Select(e => e.CommandLine)]);
        foreach (var rewriter in _runtime.TranslationRegistry.Rewriters)
        {
            if (settings.Disabled.Contains(rewriter.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            RewriteResult? result;
            try
            {
                result = rewriter.Rewrite(current, context);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _runtime.Log.Warn("translate", $"Rewriter '{rewriter.Name}' failed on input", ex);
                continue;
            }

            if (result is not null && result.Rewritten != current)
            {
                current = result.Rewritten;
                if (result.Explanation is { } explanation && !explanations.Contains(explanation))
                {
                    explanations.Add(explanation);
                }
            }
        }

        return new TranslationOutcome(current, explanations.Count == 0 ? null : string.Join("; ", explanations)) { Changed = current != input };
    }

    public string BuildShimScript()
    {
        var disabled = _runtime.Config.Current.Translation.Disabled;
        var sb = new StringBuilder();
        foreach (var shim in _runtime.TranslationRegistry.Shims)
        {
            if (!AliasNames.IsValid(shim.Name) || disabled.Contains(shim.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // A built-in alias of the same name would win over the function (aliases resolve first).
            sb.Append("Remove-Item -LiteralPath 'Alias:\\").Append(shim.Name).AppendLine("' -Force -ErrorAction Ignore");
            sb.Append("function global:").Append(shim.Name).AppendLine(" {").AppendLine(shim.FunctionBody).AppendLine("}");
        }

        return sb.ToString();
    }

    /// <summary>Load the shim module now; <paramref name="force"/> also loads it off Windows (tests, `pk translate on --shims`).</summary>
    public bool LoadShims(bool force = false)
    {
        lock (_gate)
        {
            _forceShims |= force;
        }

        return Reconcile();
    }

    internal void RequestReconcile(bool? forceShims = null)
    {
        lock (_gate)
        {
            if (forceShims is { } force)
            {
                _forceShims = force;
            }
        }

        _dirty = true;
        Reconcile();
    }

    /// <summary>Bring the loaded module in line with config. Returns false if it had to be deferred (runspace busy).</summary>
    internal bool Reconcile()
    {
        var settings = _runtime.Config.Current.Translation;
        bool force;
        lock (_gate)
        {
            force = _forceShims;
        }

        var desired = settings.Enabled && (OperatingSystem.IsWindows() || force);
        var disabled = settings.Disabled.Select(d => d.Trim()).Where(d => d.Length > 0).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var key = desired ? string.Join(",", disabled) + ";" + settings.PreferNativeBinaries : null;
        if (key == _appliedKey)
        {
            _dirty = false;
            return true;
        }

        if (!RunspaceGate.CanInvoke(_runtime))
        {
            _dirty = true;
            return false;
        }

        var options = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["Enabled"] = desired,
            ["Disabled"] = disabled,
            ["PreferNativeBinaries"] = settings.PreferNativeBinaries,
            ["Skip"] = _runtime.Aliases.All.Select(a => a.Name).ToArray(),
        };

        var script = UnloadScript + (desired ? "\nImport-Module -Name $path -ArgumentList $options -Global -Force -DisableNameChecking" : string.Empty);
        var path = Path.Combine(EmbeddedModules.Extract(_runtime.Paths, _runtime.Log), ModuleName, ModuleName + ".psd1");
        RunspaceGate.TryInvoke(_runtime, script, new Dictionary<string, object?> { ["path"] = path, ["options"] = options }, out var result);
        foreach (var error in result.Errors)
        {
            _runtime.Log.Warn("translate", $"Shim module: {error}");
        }

        _appliedKey = key;
        _dirty = false;
        return true;
    }

    internal CommandLookup? LookupCommand(string name)
    {
        try
        {
            var result = RunspaceGate.Query(
                _runtime,
                "param($n) Get-Command -Name $n -ErrorAction Ignore | Select-Object -First 1 | ForEach-Object { $_.CommandType.ToString(); [string]$_.Source }",
                new Dictionary<string, object?> { ["n"] = name });
            return result.Output.Count == 0
                ? null
                : new CommandLookup(result.Output[0]?.ToString() ?? string.Empty, result.Output.Count > 1 ? result.Output[1]?.ToString() : null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Management.Automation.RuntimeException)
        {
            _runtime.Log.Debug("translate", $"Command lookup for '{name}' failed: {ex.Message}");
            return null;
        }
    }

    internal IEnumerable<object> Describe()
    {
        var settings = _runtime.Config.Current.Translation;
        foreach (var rewriter in _runtime.TranslationRegistry.Rewriters)
        {
            yield return new
            {
                Kind = "rewriter",
                rewriter.Name,
                Description = RewriterDescriptions.GetValueOrDefault(rewriter.Name, rewriter.GetType().Name),
                Active = settings.Enabled && !settings.Disabled.Contains(rewriter.Name, StringComparer.OrdinalIgnoreCase),
            };
        }

        foreach (var (name, description) in ShimCatalog.Shims)
        {
            yield return new
            {
                Kind = "shim",
                Name = name,
                Description = description,
                Active = ShimsLoaded && !settings.Disabled.Contains(name, StringComparer.OrdinalIgnoreCase),
            };
        }

        foreach (var shim in _runtime.TranslationRegistry.Shims)
        {
            yield return new
            {
                Kind = "shim",
                shim.Name,
                shim.Description,
                Active = settings.Enabled && !settings.Disabled.Contains(shim.Name, StringComparer.OrdinalIgnoreCase),
            };
        }
    }

    public void Dispose()
    {
        _runtime.Config.Changed -= OnConfigChanged;
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e) => _dirty = true;

    private ValueTask OnIdle(HookEvent hookEvent, CancellationToken cancellationToken)
    {
        if (_dirty)
        {
            Reconcile();
        }

        return ValueTask.CompletedTask;
    }
}
