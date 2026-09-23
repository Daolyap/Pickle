using System.Collections.Concurrent;
using Pickle.Abstractions;

namespace Pickle.Core.Registries;

public sealed class PickleServices : IPickleServices
{
    private readonly ConcurrentDictionary<Type, object> _services = new();

    public void Add<T>(T implementation) where T : class => _services[typeof(T)] = implementation;

    public T? Get<T>() where T : class => _services.TryGetValue(typeof(T), out var s) ? (T)s : null;

    public T Require<T>() where T : class =>
        Get<T>() ?? throw new InvalidOperationException($"Service {typeof(T).Name} is not registered.");
}

public sealed class PromptSegmentRegistry : IPromptSegmentRegistry
{
    private readonly ConcurrentDictionary<string, IPromptSegment> _segments = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IPromptSegment> All => _segments.Values.ToList();

    public void Register(IPromptSegment segment) => _segments[segment.Type] = segment;

    public IPromptSegment? Get(string type) => _segments.TryGetValue(type, out var s) ? s : null;
}

public sealed class CompletionRegistry : ICompletionRegistry
{
    private readonly object _gate = new();
    private List<ICompletionProvider> _providers = [];

    public IReadOnlyList<ICompletionProvider> Providers
    {
        get
        {
            lock (_gate)
            {
                return _providers;
            }
        }
    }

    public void Register(ICompletionProvider provider)
    {
        lock (_gate)
        {
            _providers = [.. _providers.Where(p => p.Name != provider.Name).Append(provider).OrderByDescending(p => p.Priority)];
        }
    }
}

public sealed class KeyBindingRegistry : IKeyBindingRegistry
{
    private readonly ConcurrentDictionary<string, EditorActionInfo> _actions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _bindings = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Bindings => _bindings;

    public IReadOnlyCollection<EditorActionInfo> Actions => _actions.Values.ToList();

    public void RegisterAction(string name, string description, EditorAction handler) =>
        _actions[name] = new EditorActionInfo(name, description, handler);

    public void Bind(string chord, string actionName) => _bindings[Normalize(chord)] = actionName;

    public void Unbind(string chord) => _bindings.TryRemove(Normalize(chord), out _);

    public EditorActionInfo? GetAction(string name) => _actions.TryGetValue(name, out var a) ? a : null;

    /// <summary>Action name bound to this key, or null.</summary>
    public string? Lookup(ConsoleKeyInfo key) =>
        _bindings.TryGetValue(KeyChord.FromKeyInfo(key).ToString(), out var action) ? action : null;

    private static string Normalize(string chord) => KeyChord.TryParse(chord, out var c) ? c.ToString() : chord;
}

public sealed class CommandRegistry : ICommandRegistry
{
    private readonly ConcurrentDictionary<string, IPickleCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IPickleCommand> All => _commands.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public void Register(IPickleCommand command) => _commands[command.Name] = command;

    public IPickleCommand? Get(string name) => _commands.TryGetValue(name, out var c) ? c : null;
}

public sealed class PanelRegistry : IPanelRegistry
{
    private readonly ConcurrentDictionary<string, PanelDescriptor> _panels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ListPanelSpec> _lists = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PanelDescriptor> All => _panels.Values.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<ListPanelSpec> ListPanels => _lists.Values.ToList();

    /// <summary>Raised when a panel is registered so the line editor can bind its DefaultKey.</summary>
    public event EventHandler<PanelDescriptor>? Registered;

    public event EventHandler<ListPanelSpec>? ListRegistered;

    public void Register(PanelDescriptor panel)
    {
        _panels[panel.Id] = panel;
        Registered?.Invoke(this, panel);
    }

    public void RegisterList(ListPanelSpec spec)
    {
        _lists[spec.Id] = spec;
        ListRegistered?.Invoke(this, spec);
    }

    public PanelDescriptor? Get(string id) => _panels.TryGetValue(id, out var p) ? p : null;
}

public sealed class WizardRegistry : IWizardRegistry
{
    private readonly ConcurrentDictionary<string, WizardDefinition> _wizards = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<WizardDefinition> All => _wizards.Values.OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase).ToList();

    public void Register(WizardDefinition wizard) => _wizards[wizard.Id] = wizard;

    public WizardDefinition? Get(string id) => _wizards.TryGetValue(id, out var w) ? w : null;

    public WizardDefinition? FindForCommand(string commandName)
    {
        var name = Path.GetFileName(commandName.Trim().Trim('"', '\''));
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        foreach (var w in _wizards.Values)
        {
            if (string.Equals(w.Command, name, StringComparison.OrdinalIgnoreCase)
                || w.Aliases.Any(a => string.Equals(a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a, name, StringComparison.OrdinalIgnoreCase)))
            {
                return w;
            }
        }

        return null;
    }
}

public sealed class TranslationRegistry : ITranslationRegistry
{
    private readonly object _gate = new();
    private List<IInputRewriter> _rewriters = [];
    private List<TranslationShim> _shims = [];

    public IReadOnlyList<IInputRewriter> Rewriters
    {
        get
        {
            lock (_gate)
            {
                return _rewriters;
            }
        }
    }

    public IReadOnlyList<TranslationShim> Shims
    {
        get
        {
            lock (_gate)
            {
                return _shims;
            }
        }
    }

    public void RegisterRewriter(IInputRewriter rewriter)
    {
        lock (_gate)
        {
            _rewriters = [.. _rewriters.Where(r => r.Name != rewriter.Name).Append(rewriter).OrderBy(r => r.Order)];
        }
    }

    public void RegisterShim(TranslationShim shim)
    {
        lock (_gate)
        {
            _shims = [.. _shims.Where(s => !string.Equals(s.Name, shim.Name, StringComparison.OrdinalIgnoreCase)).Append(shim)];
        }
    }
}

public sealed class HookRegistry : IHookRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<HookKind, List<Func<HookEvent, CancellationToken, ValueTask>>> _handlers = [];
    private readonly IPickleLogger _log;

    public HookRegistry(IPickleLogger log) => _log = log;

    public IDisposable Register(HookKind kind, Func<HookEvent, CancellationToken, ValueTask> handler)
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(kind, out var list))
            {
                _handlers[kind] = list = [];
            }

            list.Add(handler);
        }

        return new Unregister(() =>
        {
            lock (_gate)
            {
                _handlers[kind].Remove(handler);
            }
        });
    }

    public async ValueTask RaiseAsync(HookEvent hookEvent, CancellationToken cancellationToken = default)
    {
        Func<HookEvent, CancellationToken, ValueTask>[] snapshot;
        lock (_gate)
        {
            snapshot = _handlers.TryGetValue(hookEvent.Kind, out var list) ? [.. list] : [];
        }

        foreach (var handler in snapshot)
        {
            try
            {
                await handler(hookEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("hooks", $"{hookEvent.Kind} hook failed: {ex.Message}", ex);
            }
        }
    }

    private sealed class Unregister(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
