using System.Globalization;
using System.Text;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Windows;

/// <summary>Task Scheduler panel (Alt+S): folder tree, task table, details (triggers, actions, history) and actions.</summary>
public sealed class SchedulerPanel : WindowsPanelBase
{
    private const string Root = @"\";
    private const string PickleFolder = @"\Pickle";

    private readonly ITaskSchedulerService? _scheduler;
    private readonly TreeView<string> _tree;
    private readonly TableView _tasksTable = new() { Width = Dim.Fill(), Height = Dim.Percent(55), FullRowSelect = true };
    private readonly TextPane _details = MakeText("Details");
    private List<string> _folders = [Root];
    private List<ScheduledTaskInfo> _tasks = [];
    private string _folder = PickleFolder;

    public SchedulerPanel(PanelContext context)
        : base(context, "Task Scheduler")
    {
        _scheduler = Service<ITaskSchedulerService>();
        _tree = new TreeView<string>(new DelegateTreeBuilder<string>(Children, f => Children(f).Any()))
        {
            Width = 28,
            Height = Dim.Fill(),
            BorderStyle = Terminal.Gui.Drawing.LineStyle.Single,
            Title = "Folders",
            AspectGetter = f => f == Root ? @"\ (root)" : f[(f.LastIndexOf('\\') + 1)..],
        };
        if (_scheduler is not { IsSupported: true })
        {
            Body.Add(new Label { X = 1, Y = 1, Text = "Task Scheduler is only available on Windows." });
            return;
        }

        _tree.SelectionChanged += (_, e) =>
        {
            if (e.NewValue is { } folder && folder != _folder)
            {
                _folder = folder;
                LoadTasks();
            }
        };
        var right = new View { X = Pos.Right(_tree), Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _tasksTable.ValueChanged += (_, _) => ShowDetails();
        _details.Y = Pos.Bottom(_tasksTable);
        _details.Height = Dim.Fill();
        right.Add(_tasksTable, _details);
        Body.Add(_tree, right);

        AddHint(Key.F5, "Refresh", Refresh);
        AddHint(Key.F2, "Run", () => Act("run"));
        AddHint(Key.F3, "Stop", () => Act("stop"));
        AddHint(Key.F4, "Enable/Disable", () => Act("toggle"));
        AddHint(Key.F8, "Delete", () => Act("delete"));
        AddHint(Key.F7, "New task", NewTask);
    }

    internal IReadOnlyList<string> Folders => _folders;

    internal IReadOnlyList<ScheduledTaskInfo> Tasks => _tasks;

    internal string CurrentFolder => _folder;

    internal string DetailsText => _details.Content;

    internal TableView TasksTable => _tasksTable;

    protected override void Opened() => Refresh();

    internal void Refresh()
    {
        if (_scheduler is null)
        {
            return;
        }

        Load(ct => _scheduler.GetFoldersAsync(Root, ct), ApplyFolders, "loading folders…");
        LoadTasks();
    }

    internal void ApplyFolders(IReadOnlyList<string> folders)
    {
        var all = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { Root };
        foreach (var folder in folders)
        {
            for (var f = folder.TrimEnd('\\'); f.Length > 0; f = Parent(f))
            {
                all.Add(f);
                if (f == Root)
                {
                    break;
                }
            }
        }

        _folders = [.. all];
        _tree.ClearObjects();
        _tree.AddObject(Root);
        _tree.Expand(Root);
        if (_folders.Contains(_folder, StringComparer.OrdinalIgnoreCase))
        {
            _tree.SelectedObject = _folders.First(f => string.Equals(f, _folder, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal void SelectFolder(string folder)
    {
        _folder = folder;
        LoadTasks();
    }

    internal void ApplyTasks(IReadOnlyList<ScheduledTaskInfo> tasks)
    {
        _tasks = [.. tasks.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
        _tasksTable.Table = new EnumerableTableSource<ScheduledTaskInfo>(_tasks, new Dictionary<string, Func<ScheduledTaskInfo, object>>
        {
            ["Name"] = t => t.Name,
            ["State"] = t => t.Enabled ? t.State : "Disabled",
            ["Last run"] = t => When(t.LastRunTime),
            ["Result"] = t => t.LastResult is { } r ? "0x" + r.ToString("X", CultureInfo.InvariantCulture) : string.Empty,
            ["Next run"] = t => When(t.NextRunTime),
        });
        _tasksTable.Update();
        ShowDetails();
    }

    internal ScheduledTaskInfo? SelectedTask
    {
        get
        {
            var row = SelectedRow(_tasksTable);
            return row >= 0 && row < _tasks.Count ? _tasks[row] : _tasks.FirstOrDefault();
        }
    }

    internal void Act(string action)
    {
        if (_scheduler is null || SelectedTask is not { } task)
        {
            return;
        }

        var path = task.Path;
        Func<CancellationToken, Task>? work = action switch
        {
            "run" => ct => _scheduler.RunAsync(path, ct),
            "stop" => ct => _scheduler.StopAsync(path, ct),
            "toggle" when Ask(task.Enabled ? "Disable task" : "Enable task", $"{(task.Enabled ? "Disable" : "Enable")} {path}?") =>
                ct => _scheduler.SetEnabledAsync(path, !task.Enabled, ct),
            "delete" when Ask("Delete task", $"Delete {path}?\n\nThis cannot be undone.") => ct => _scheduler.DeleteAsync(path, ct),
            _ => null,
        };
        if (work is not null)
        {
            Run(work, LoadTasks, action + "…");
        }
    }

    internal void NewTask()
    {
        if (App is not { } app)
        {
            return;
        }

        using var dialog = new NewTaskDialog();
        app.Run(dialog);
        if (dialog.Confirmed)
        {
            CreateTask(dialog.Values);
        }
    }

    internal void CreateTask(NewTaskValues values)
    {
        if (_scheduler is null)
        {
            return;
        }

        ScheduledTaskDefinition definition;
        try
        {
            definition = values.ToDefinition(_scheduler, Pickle.Shell.CurrentDirectory);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            Fail(ex.Message);
            return;
        }

        if (definition.RunElevated && !Ask("Create elevated task", $"Create {definition.Folder}\\{definition.Name} with highest privileges?\n\nWindows will ask for administrator permission (UAC)."))
        {
            return;
        }

        Load(
            ct => _scheduler.CreateAsync(definition, ct),
            info =>
            {
                Tell("Task Scheduler", $"Created {info.Path}.");
                _folder = info.Folder;
                Refresh();
            },
            "creating task…");
    }

    private IEnumerable<string> Children(string folder) =>
        _folders.Where(f => f != Root && string.Equals(Parent(f), folder, StringComparison.OrdinalIgnoreCase));

    private static string Parent(string folder)
    {
        var index = folder.TrimEnd('\\').LastIndexOf('\\');
        return index <= 0 ? Root : folder[..index];
    }

    private void LoadTasks()
    {
        if (_scheduler is null)
        {
            return;
        }

        var folder = _folder;
        Load(
            ct => _scheduler.GetTasksAsync(folder, false, ct),
            tasks =>
            {
                // A reload started by an action can finish after the user moved to another folder.
                if (string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
                {
                    ApplyTasks(tasks);
                }
            },
            "loading tasks…");
    }

    private void ShowDetails()
    {
        if (SelectedTask is not { } task)
        {
            _details.Content = _tasks.Count == 0 ? $"No tasks in {_folder}. Press F7 to create one." : string.Empty;
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(task.Path);
        sb.AppendLine($"State: {task.State}{(task.Enabled ? string.Empty : " (disabled)")}   Highest privileges: {(task.RunElevated ? "yes" : "no")}");
        sb.AppendLine($"Author: {task.Author ?? "—"}");
        if (task.Description is { Length: > 0 } description)
        {
            sb.AppendLine($"Description: {description}");
        }

        sb.AppendLine("Triggers:");
        foreach (var trigger in task.Triggers)
        {
            sb.AppendLine("  " + trigger);
        }

        sb.AppendLine("Actions:");
        foreach (var action in task.Actions)
        {
            sb.AppendLine("  " + action);
        }

        _details.Content = sb.ToString();
        if (_scheduler is not null)
        {
            var path = task.Path;
            Load(
                ct => _scheduler.GetHistoryAsync(path, 10, ct),
                runs =>
                {
                    if (SelectedTask?.Path != path || runs.Count == 0)
                    {
                        return;
                    }

                    var history = string.Join("\n", runs.Select(r => $"  {When(r.Time)}  {r.Event}{(r.ResultCode is { } c ? $" (0x{c:X})" : string.Empty)}"));
                    _details.Content = sb + "History:\n" + history;
                },
                "history…");
        }
    }
}
