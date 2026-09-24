using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Pickle.Abstractions;
using Pickle.Core.Contracts;

namespace Pickle.Core.Completion;

/// <summary>
/// Tab completion: PowerShell's <see cref="CommandCompletion.CompleteInput(string, int, System.Collections.Hashtable, PowerShell)"/>
/// on the interactive runspace (only while it is idle, with a timeout) merged with registered
/// <see cref="ICompletionProvider"/>s. Also owns the <c>complete</c>/<c>complete-previous</c> editor actions.
/// </summary>
public sealed class CompletionEngine : ICompletionEngine, IRuntimeComponent
{
    private readonly PickleRuntime _runtime;
    private InlineCycle? _cycle;

    public CompletionEngine(PickleRuntime runtime) => _runtime = runtime;

    /// <summary>How long PowerShell may take before it is stopped and only provider results are used.</summary>
    internal TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(1500);

    public void Initialize()
    {
        _runtime.CompletionRegistry.Register(new PickleCommandCompletionProvider(_runtime.CommandRegistry));
        _runtime.CompletionRegistry.Register(new AliasCompletionProvider(_runtime.Aliases));
        var keys = _runtime.KeyBindingRegistry;
        keys.RegisterAction(EditorActionNames.Complete, "Complete the word at the cursor (menu when ambiguous)", (buffer, ct) => CompleteInteractiveAsync(buffer, previous: false, ct));
        keys.RegisterAction(EditorActionNames.CompletePrevious, "Open the completion menu at its last item", (buffer, ct) => CompleteInteractiveAsync(buffer, previous: true, ct));
    }

    public async Task<CompletionSet> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cursor = Math.Clamp(request.Cursor, 0, request.Input.Length);
        request = request with { Cursor = cursor };
        var providers = _runtime.CompletionRegistry.Providers;

        foreach (var provider in providers.OfType<IExclusiveCompletionProvider>())
        {
            if (await RunProviderAsync(provider, request, cancellationToken).ConfigureAwait(false) is { Items.Count: > 0 } exclusive)
            {
                return exclusive;
            }
        }

        var others = providers.Where(p => p is not IExclusiveCompletionProvider).ToList();
        using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        providerCts.CancelAfter(Timeout);
        var providerTasks = others.Select(p => Task.Run(() => RunProviderAsync(p, request, providerCts.Token), CancellationToken.None)).ToArray();

        var primary = await CompletePowerShellAsync(request, cancellationToken).ConfigureAwait(false);

        var all = Task.WhenAll(providerTasks);
        await Task.WhenAny(all, Task.Delay(Timeout, CancellationToken.None)).ConfigureAwait(false);
        var sets = new List<(int Priority, CompletionSet Set)>();
        for (var i = 0; i < others.Count; i++)
        {
            if (providerTasks[i].IsCompletedSuccessfully && providerTasks[i].Result is { } set)
            {
                sets.Add((others[i].Priority, set));
            }
        }

        return Merge(primary, sets, cursor);
    }

    /// <summary>
    /// PowerShell's set (priority 0) is the base when it has items; otherwise the highest-priority non-empty provider
    /// set. Sets with the same replacement range as the base are merged by priority and de-duplicated by
    /// CompletionText (first wins); sets for a different range are dropped.
    /// </summary>
    internal static CompletionSet Merge(CompletionSet? primary, IReadOnlyList<(int Priority, CompletionSet Set)> providerSets, int cursor)
    {
        var parts = new List<(int Priority, int Order, CompletionSet Set)>();
        CompletionSet? baseSet = null;
        if (primary is { Items.Count: > 0 })
        {
            baseSet = primary;
            parts.Add((0, -1, primary));
        }
        else
        {
            foreach (var (_, set) in providerSets.OrderByDescending(s => s.Priority))
            {
                if (set.Items.Count > 0)
                {
                    baseSet = set;
                    break;
                }
            }
        }

        if (baseSet is null)
        {
            return CompletionSet.Empty(cursor);
        }

        for (var i = 0; i < providerSets.Count; i++)
        {
            var (priority, set) = providerSets[i];
            if (set.Items.Count > 0 && set.ReplacementIndex == baseSet.ReplacementIndex && set.ReplacementLength == baseSet.ReplacementLength)
            {
                parts.Add((priority, i, set));
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<CompletionItem>();
        foreach (var part in parts.OrderByDescending(p => p.Priority).ThenBy(p => p.Order))
        {
            foreach (var item in part.Set.Items)
            {
                if (seen.Add(item.CompletionText))
                {
                    items.Add(item);
                }
            }
        }

        return new CompletionSet(baseSet.ReplacementIndex, baseSet.ReplacementLength, items);
    }

    private async Task<CompletionSet?> RunProviderAsync(ICompletionProvider provider, CompletionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.GetCompletionsAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _runtime.Log.Warn("completion", $"Completion provider '{provider.Name}' failed", ex);
            return null;
        }
    }

    // ───────────── PowerShell ─────────────

    private async Task<CompletionSet?> CompletePowerShellAsync(CompletionRequest request, CancellationToken cancellationToken)
    {
        var engine = _runtime.Engine;
        if (!engine.IsOpen || engine.IsExecuting)
        {
            return null;
        }

        // A pushed (remote) runspace completes against the remote session, like pwsh does.
        var runspace = engine.Host.Runspace;
        if (runspace.RunspaceStateInfo.State != RunspaceState.Opened || runspace.RunspaceAvailability != RunspaceAvailability.Available)
        {
            return null;
        }

        // Hold the engine's runspace lock so background InvokeAsync calls can't start a pipeline mid-completion.
        if (!engine.TryEnterMain(TimeSpan.Zero))
        {
            return null;
        }

        var ps = PowerShell.Create();
        ps.Runspace = runspace;
        var work = Task.Run(() => CommandCompletion.CompleteInput(request.Input, request.Cursor, null, ps), CancellationToken.None);
        try
        {
            var winner = await Task.WhenAny(work, Task.Delay(Timeout, cancellationToken)).ConfigureAwait(false);
            if (winner != work)
            {
                _runtime.Log.Debug("completion", $"PowerShell completion exceeded {Timeout.TotalMilliseconds} ms; stopping it");
                StopQuietly(ps);

                // The runspace must be idle again before the REPL runs the next pipeline.
                await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
                return null;
            }

            return Map(await work.ConfigureAwait(false), request.Cursor);
        }
        catch (Exception ex) when (ex is InvalidOperationException or RuntimeException or InvalidRunspaceStateException or ArgumentException)
        {
            // Typically "a pipeline is already running": another component used the runspace concurrently.
            _runtime.Log.Debug("completion", $"PowerShell completion failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (work.IsCompleted)
            {
                ps.Dispose();
                engine.ExitMain();
            }
            else
            {
                _ = work.ContinueWith(
                    _ =>
                    {
                        ps.Dispose();
                        engine.ExitMain();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
        }
    }

    private static void StopQuietly(PowerShell ps)
    {
        try
        {
            ps.Stop();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    internal static CompletionSet Map(CommandCompletion completion, int cursor)
    {
        var matches = completion.CompletionMatches;
        if (matches is null || matches.Count == 0 || completion.ReplacementIndex < 0)
        {
            return CompletionSet.Empty(cursor);
        }

        var items = new List<CompletionItem>(matches.Count);
        foreach (var result in matches)
        {
            var kind = MapKind(result.ResultType);
            var list = string.IsNullOrEmpty(result.ListItemText) ? result.CompletionText : result.ListItemText;
            items.Add(new CompletionItem(result.CompletionText, list, kind, Describe(result, kind)));
        }

        return new CompletionSet(completion.ReplacementIndex, Math.Max(0, completion.ReplacementLength), items);
    }

    internal static CompletionKind MapKind(CompletionResultType type) => type switch
    {
        CompletionResultType.Command => CompletionKind.Command,
        CompletionResultType.ParameterName => CompletionKind.Parameter,
        CompletionResultType.ParameterValue => CompletionKind.ParameterValue,
        CompletionResultType.ProviderItem => CompletionKind.File,
        CompletionResultType.ProviderContainer => CompletionKind.Directory,
        CompletionResultType.Variable => CompletionKind.Variable,
        CompletionResultType.Property => CompletionKind.Property,
        CompletionResultType.Method => CompletionKind.Method,
        CompletionResultType.Type or CompletionResultType.Namespace => CompletionKind.Type,
        CompletionResultType.Keyword or CompletionResultType.DynamicKeyword => CompletionKind.Keyword,
        CompletionResultType.History => CompletionKind.History,
        CompletionResultType.Text => CompletionKind.Text,
        _ => CompletionKind.Other,
    };

    /// <summary>A one-line description for the menu, derived from the tooltip (null when it adds nothing).</summary>
    internal static string? Describe(CompletionResult result, CompletionKind kind)
    {
        var tooltip = result.ToolTip ?? string.Empty;
        var first = tooltip.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (first is null)
        {
            return null;
        }

        switch (kind)
        {
            case CompletionKind.File or CompletionKind.Directory:
                return null;
            case CompletionKind.Parameter:
                // "[switch] Force" → "[switch]"
                var close = first.IndexOf(']', StringComparison.Ordinal);
                return first.StartsWith('[') && close > 0 ? first[..(close + 1)] : null;
            case CompletionKind.Variable when first.StartsWith('['):
                // "[PSVersionHashTable]$PSVersionTable - Version information..." → "[PSVersionHashTable] Version information..."
                var typeEnd = first.IndexOf(']', StringComparison.Ordinal);
                var dash = first.IndexOf(" - ", StringComparison.Ordinal);
                return typeEnd < 0 ? null : dash > 0 ? first[..(typeEnd + 1)] + " " + first[(dash + 3)..] : first[..(typeEnd + 1)];
            case CompletionKind.Command:
                // Functions/cmdlets carry a multi-line syntax block (too long for a column); aliases their target;
                // applications their path.
                if (tooltip.Contains('\n', StringComparison.Ordinal) || first.Contains(' ', StringComparison.Ordinal))
                {
                    return null;
                }

                if (first.Equals(result.CompletionText, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return first.IndexOfAny(['/', '\\']) >= 0 ? first : "→ " + first;
        }

        return first == result.CompletionText || first == result.ListItemText ? null : first;
    }

    // ───────────── Editor actions ─────────────

    internal async ValueTask CompleteInteractiveAsync(IEditorBuffer buffer, bool previous, CancellationToken cancellationToken)
    {
        var text = buffer.Text;
        var cursor = Math.Clamp(buffer.Cursor, 0, text.Length);
        var settings = _runtime.Config.Current.Editor;

        if (!settings.CompletionMenu && _cycle is { } cycle && cycle.AppliedText == text && cycle.AppliedCursor == cursor)
        {
            cycle.Index = (cycle.Index + (previous ? -1 : 1) + cycle.Set.Items.Count) % cycle.Set.Items.Count;
            ApplyCycle(buffer, cycle);
            return;
        }

        _cycle = null;
        var set = await CompleteAsync(new CompletionRequest(text, cursor, _runtime.Engine.CurrentDirectory), cancellationToken).ConfigureAwait(false);
        if (set.Items.Count == 0)
        {
            return;
        }

        if (set.Items.Count == 1)
        {
            var (newText, newCursor) = ApplyReplacement(text, set, set.Items[0].CompletionText);
            buffer.Replace(newText, newCursor);
            return;
        }

        if (CommonPrefix(text, set) is { } common)
        {
            var (newText, newCursor) = ApplyReplacement(text, set, common);
            buffer.Replace(newText, newCursor);
            return;
        }

        if (!settings.CompletionMenu)
        {
            _cycle = new InlineCycle(set, text, previous ? set.Items.Count - 1 : 0);
            ApplyCycle(buffer, _cycle);
            return;
        }

        buffer.OpenOverlay(new CompletionMenu(set, text, cursor, _runtime.Themes.Current.Ui, settings.CompletionMenuMaxRows, selectLast: previous));
    }

    internal static (string Text, int Cursor) ApplyReplacement(string text, CompletionSet set, string replacement)
    {
        var start = Math.Clamp(set.ReplacementIndex, 0, text.Length);
        var end = Math.Clamp(start + set.ReplacementLength, start, text.Length);
        return (string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(end)), start + replacement.Length);
    }

    /// <summary>The case-insensitive common prefix of all items when it extends what was typed (never for quoted items).</summary>
    internal static string? CommonPrefix(string text, CompletionSet set)
    {
        var items = set.Items;
        if (items.Count == 0 || items.Any(i => i.CompletionText.Length == 0 || i.CompletionText[0] is '\'' or '"' or '&'))
        {
            return null;
        }

        var start = Math.Clamp(set.ReplacementIndex, 0, text.Length);
        var typed = text.AsSpan(start, Math.Clamp(set.ReplacementLength, 0, text.Length - start));
        var first = items[0].CompletionText;
        var length = first.Length;
        for (var i = 1; i < items.Count && length > typed.Length; i++)
        {
            var other = items[i].CompletionText;
            var max = Math.Min(length, other.Length);
            var j = 0;
            while (j < max && char.ToUpperInvariant(first[j]) == char.ToUpperInvariant(other[j]))
            {
                j++;
            }

            length = j;
        }

        if (length <= typed.Length || !first.AsSpan(0, length).StartsWith(typed, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return first[..length];
    }

    private static void ApplyCycle(IEditorBuffer buffer, InlineCycle cycle)
    {
        var (text, cursor) = ApplyReplacement(cycle.OriginalText, cycle.Set, cycle.Set.Items[cycle.Index].CompletionText);
        buffer.Replace(text, cursor);
        cycle.AppliedText = text;
        cycle.AppliedCursor = cursor;
    }

    /// <summary>PowerShell-style Tab cycling used when the completion menu is disabled.</summary>
    private sealed class InlineCycle(CompletionSet set, string originalText, int index)
    {
        public CompletionSet Set { get; } = set;

        public string OriginalText { get; } = originalText;

        public int Index { get; set; } = index;

        public string? AppliedText { get; set; }

        public int AppliedCursor { get; set; }
    }
}
