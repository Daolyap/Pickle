using Pickle.Abstractions;
using Pickle.Tui.Widgets;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Pickle.Tui;

/// <summary>What an action on a resource did; its message is shown in the details pane.</summary>
public sealed record ActionOutcome(bool Ok, string? Message = null)
{
    /// <summary>The action needs a password on the terminal: the panel offers to close and run this line in the shell.</summary>
    public string? ShellCommand { get; init; }

    public static ActionOutcome Done(string? message = null) => new(true, message);

    public static ActionOutcome Fail(string message) => new(false, message);
}

/// <summary>
/// A list of things (containers, pods, services, packages…) on the left, details of the selected one on the right, and
/// actions on F-keys that run in the background and then refresh. Derive, implement <see cref="LoadAsync"/> and add
/// actions in the constructor:
/// <code>
/// AddAction(Key.F2, "Stop", (c, ct) => service.StopAsync(c.Id, ct), confirm: c => $"Stop {c.Name}?");
/// AddCommand(Key.F4, "Logs", c => PowerShellQuote.Command("docker", "logs", "-f", c.Id));
/// </code>
/// </summary>
public abstract class ResourcePanel<T> : PanelWindow
    where T : class
{
    private readonly Func<T, string> _text;
    private CancellationTokenSource? _describing;

    protected ResourcePanel(PanelContext context, string title, Func<T, string> text, string detailsTitle = "Details")
        : base(context, title)
    {
        _text = text;
        DetailsTitle = detailsTitle;
        List = new FilterableList<T>(text)
        {
            Width = Dim.Percent(46),
            Hint = Hint,
            Detail = Detail,
            Category = Category,
            ItemColor = ItemColor,
            Schemes = Schemes,
        };
        Details = new PreviewPane(detailsTitle) { X = Pos.Right(List), Width = Dim.Fill(), Height = Dim.Fill(), Schemes = Schemes };
        Body.Add(List, Details);

        List.SelectionChanged += (_, item) => Describe(item);
        List.ItemAccepted += (_, item) => OnAccepted(item);
        AddHint(Key.F5, "Refresh", Reload);
        List.Filter.SetFocus();
    }

    public FilterableList<T> List { get; }

    public PreviewPane Details { get; }

    protected string DetailsTitle { get; }

    /// <summary>Redraw the list this often while the panel is open (null: only on F5).</summary>
    protected virtual TimeSpan? AutoRefresh => null;

    /// <summary>Shown in the details pane when there is nothing to list.</summary>
    protected virtual string EmptyMessage => "Nothing to show.";

    protected virtual string? Hint(T item) => null;

    protected virtual string? Detail(T item) => null;

    protected virtual string? Category(T item) => null;

    protected virtual Terminal.Gui.Drawing.Color? ItemColor(T item) => null;

    /// <summary>Identity across refreshes, so the selection survives (default: the displayed text).</summary>
    protected virtual string KeyOf(T item) => _text(item);

    protected abstract Task<IReadOnlyList<T>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>The details pane's lines for <paramref name="item"/> (may call a slow program).</summary>
    protected virtual Task<IReadOnlyList<string>> DescribeAsync(T item, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([_text(item)]);

    /// <summary>Enter on an item (default: nothing).</summary>
    protected virtual void OnAccepted(T item)
    {
    }

    protected override void OnOpened()
    {
        Reload();
        if (AutoRefresh is { } interval)
        {
            Every(interval, Reload);
        }
    }

    protected void Reload()
    {
        var selected = List.Selected is { } current ? KeyOf(current) : null;
        RunInBackground(
            LoadAsync,
            items =>
            {
                List.SetItems(items);
                if (selected is not null && items.FirstOrDefault(i => KeyOf(i) == selected) is { } same)
                {
                    List.Select(same);
                }

                if (items.Count == 0)
                {
                    Details.ShowMessage(DetailsTitle, EmptyMessage);
                }
            },
            "loading…");
    }

    /// <summary>Runs <paramref name="run"/> for the selected item off the UI thread (after <paramref name="confirm"/>), shows its message, then reloads.</summary>
    protected void AddAction(Key key, string label, Func<T, CancellationToken, Task<ActionOutcome>> run, Func<T, string?>? confirm = null, Func<T, bool>? enabled = null) =>
        AddHint(key, label, () =>
        {
            if (List.Selected is not { } item || IsClosed || enabled?.Invoke(item) == false)
            {
                return;
            }

            if (confirm?.Invoke(item) is { } question && !Confirm(label, question))
            {
                return;
            }

            RunInBackground(
                async ct => await run(item, ct).ConfigureAwait(false),
                outcome =>
                {
                    Details.ShowMessage(label, outcome.Message ?? (outcome.Ok ? "Done." : "Failed."));
                    if (outcome.ShellCommand is { Length: > 0 } line
                        && Confirm(label, $"A password is needed, so this runs in the shell:\n\n{line}\n\nClose the panel and run it?"))
                    {
                        Complete(new PanelResult(PanelResultKind.RunCommand, line));
                        return;
                    }

                    Reload();
                },
                label.ToLowerInvariant() + "…");
        });

    /// <summary>Closes the panel and runs <paramref name="commandLine"/> in the shell (logs, exec, anything interactive or streaming).</summary>
    protected void AddCommand(Key key, string label, Func<T, string?> commandLine, PanelResultKind kind = PanelResultKind.RunCommand) =>
        AddHint(key, label, () =>
        {
            if (List.Selected is { } item && !IsClosed && commandLine(item) is { Length: > 0 } line)
            {
                Complete(new PanelResult(kind, line));
            }
        });

    private void Describe(T? item)
    {
        _describing?.Cancel();
        if (item is null)
        {
            Details.Clear();
            return;
        }

        _describing = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        var token = _describing.Token;
        Details.Show(DetailsTitle, [_text(item), string.Empty, "loading…"]);
        _ = Task.Run(async () =>
        {
            try
            {
                var lines = await DescribeAsync(item, token).ConfigureAwait(false);
                OnUi(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        Details.Show(DetailsTitle, lines);
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                OnUi(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        Details.ShowMessage(DetailsTitle, ex.Message);
                    }
                });
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _describing?.Cancel();
            _describing?.Dispose();
        }

        base.Dispose(disposing);
    }
}
