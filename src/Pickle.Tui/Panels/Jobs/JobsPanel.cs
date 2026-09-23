using System.Drawing;
using System.Globalization;
using System.Management.Automation;
using Pickle.Abstractions;
using Pickle.Tui.Widgets;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Jobs;

/// <summary>A row of the jobs table (from Get-Job in the main runspace).</summary>
public sealed record JobInfo(int Id, string Name, string State, string Type, bool HasMoreData, string Start, string End, string Command);

/// <summary>
/// Jobs (Alt+J): the main runspace's PowerShell jobs, refreshed every second, with an output preview
/// (<c>Receive-Job -Keep</c>) and Stop / Remove / Receive / New job actions.
/// </summary>
public sealed class JobsPanel : PanelWindow
{
    internal const string ListScript = """
        foreach ($__pickleJob in @(Get-Job)) {
            [pscustomobject]@{
                Id = [int]$__pickleJob.Id
                Name = [string]$__pickleJob.Name
                State = [string]$__pickleJob.JobStateInfo.State
                Type = [string]$__pickleJob.PSJobTypeName
                HasMoreData = [bool]$__pickleJob.HasMoreData
                Start = if ($__pickleJob.PSBeginTime) { $__pickleJob.PSBeginTime.ToString('HH:mm:ss') } else { '' }
                End = if ($__pickleJob.PSEndTime) { $__pickleJob.PSEndTime.ToString('HH:mm:ss') } else { '' }
                Command = ([string]$__pickleJob.Command).Trim()
            }
        }
        """;

    internal const string OutputScript = "param($id) Receive-Job -Id $id -Keep 2>&1 | Select-Object -Last 500 | Out-String -Width 200";

    internal const string StopScript = "param($id) Stop-Job -Id $id";

    internal const string RemoveScript = "param($id) Remove-Job -Id $id -Force";

    internal const string NewJobScript = """
        param($command)
        $__pickleBlock = [scriptblock]::Create($command)
        if (Get-Command Start-ThreadJob -ErrorAction Ignore) { Start-ThreadJob -ScriptBlock $__pickleBlock } else { Start-Job -ScriptBlock $__pickleBlock }
        """;

    private readonly TableView _table;
    private readonly PreviewPane _output;
    private IReadOnlyList<JobInfo> _jobs = [];
    private int _refreshing;
    private (int Id, string State, bool HasMoreData)? _previewed;

    public JobsPanel(PanelContext context)
        : base(context, "Jobs")
    {
        _table = new TableView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Percent(55),
            FullRowSelect = true,
            Table = new JobTableSource([]),
        };
        _table.Style.ExpandLastColumn = true;
        _output = new PreviewPane("Output") { X = 0, Y = Pos.Bottom(_table), Width = Dim.Fill(), Height = Dim.Fill(), Schemes = Schemes };
        Body.Add(_table, _output);

        _table.ValueChanged += (_, _) => UpdatePreview(force: false);
        _table.KeyDown += (_, key) =>
        {
            if (key == Key.Enter)
            {
                Receive();
                key.Handled = true;
            }
            else if (key == Key.Delete)
            {
                Remove();
                key.Handled = true;
            }
        };

        AddHint(Key.F2, "New job", NewJob);
        AddHint(Key.F3, "Stop", Stop);
        AddHint(Key.F4, "Remove", Remove);
        AddHint(Key.F6, "Receive", Receive);
        AddHint(Key.F5, "Refresh", Refresh);
        Every(TimeSpan.FromSeconds(1), Refresh);
        _table.SetFocus();
    }

    public IReadOnlyList<JobInfo> Jobs => _jobs;

    public JobInfo? SelectedJob =>
        _table.Value is { } selection && selection.SelectedCell.Y >= 0 && selection.SelectedCell.Y < _jobs.Count ? _jobs[selection.SelectedCell.Y] : null;

    internal PreviewPane Output => _output;

    internal static JobInfo Parse(PSObject row)
    {
        string S(string name) => row.Properties[name]?.Value?.ToString() ?? string.Empty;
        return new JobInfo(
            int.TryParse(S("Id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0,
            S("Name"),
            S("State"),
            S("Type"),
            row.Properties["HasMoreData"]?.Value is true,
            S("Start"),
            S("End"),
            S("Command").ReplaceLineEndings(" "));
    }

    internal void Select(int jobId)
    {
        var index = _jobs.ToList().FindIndex(j => j.Id == jobId);
        if (index >= 0)
        {
            _table.Value = new TableSelection(new Point(0, index));
            _table.EnsureCursorIsVisible();
        }
    }

    protected override void OnOpened() => Refresh();

    private static Dictionary<string, object?> IdParameter(JobInfo job) => new() { ["id"] = job.Id };

    private void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        var token = Lifetime;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await Pickle.Shell.InvokeAsync(ListScript, null, ShellTarget.Main, token).ConfigureAwait(false);
                var jobs = result.Output.Where(o => o is not null).Select(Parse).ToList();
                OnUi(() => Apply(jobs));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Pickle.Log.Warn("jobs", "Get-Job failed", ex);
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        });
    }

    private void Apply(IReadOnlyList<JobInfo> jobs)
    {
        var selectedId = SelectedJob?.Id;
        _jobs = jobs;
        _table.Table = new JobTableSource(jobs);
        var index = selectedId is { } id ? Math.Max(0, jobs.ToList().FindIndex(j => j.Id == id)) : 0;
        _table.Value = jobs.Count > 0 ? new TableSelection(new Point(0, index)) : null;
        _table.SetNeedsDraw();
        UpdatePreview(force: false);
    }

    private void UpdatePreview(bool force)
    {
        if (SelectedJob is not { } job)
        {
            _previewed = null;
            _output.ShowMessage("Output", _jobs.Count == 0 ? "No jobs. F2 starts one." : string.Empty);
            return;
        }

        var key = (job.Id, job.State, job.HasMoreData);
        if (!force && _previewed == key)
        {
            return;
        }

        _previewed = key;
        _ = Task.Run(
            async () =>
            {
                var result = await Pickle.Shell.InvokeAsync(OutputScript, IdParameter(job), ShellTarget.Main, Lifetime).ConfigureAwait(false);
                var text = string.Concat(result.Output.Select(o => o?.ToString())).TrimEnd('\r', '\n');
                OnUi(() =>
                {
                    if (SelectedJob?.Id == job.Id)
                    {
                        var lines = text.Length == 0 ? [new PreviewLine("(no output yet)", Muted: true)] : text.Split('\n').Select(l => new PreviewLine(l.TrimEnd('\r'))).ToList();
                        _output.Show($"Output of job {job.Id} ({job.State})", lines);
                    }
                });
            },
            Lifetime);
    }

    private void Stop()
    {
        if (SelectedJob is { } job)
        {
            Run(StopScript, IdParameter(job), "stopping…");
        }
    }

    private void Remove()
    {
        if (SelectedJob is { } job && Confirm("Remove job", $"Remove job {job.Id} ({job.Name})?"))
        {
            Run(RemoveScript, IdParameter(job), "removing…");
        }
    }

    private void Receive()
    {
        if (SelectedJob is { } job)
        {
            Complete(new PanelResult(PanelResultKind.RunCommand, $"Receive-Job -Id {job.Id.ToString(CultureInfo.InvariantCulture)} -Keep"));
        }
    }

    private void NewJob()
    {
        var command = Prompt("New job", "Command to run in the background:");
        if (!string.IsNullOrWhiteSpace(command))
        {
            Run(NewJobScript, new Dictionary<string, object?> { ["command"] = command }, "starting…");
        }
    }

    private void Run(string script, Dictionary<string, object?> parameters, string busy) =>
        RunInBackground(
            ct => Pickle.Shell.InvokeAsync(script, parameters, ShellTarget.Main, ct),
            result =>
            {
                if (result.HadErrors)
                {
                    ShowError(string.Join('\n', result.Errors.Select(e => e.ToString())));
                }

                Refresh();
            },
            busy);

    private sealed class JobTableSource(IReadOnlyList<JobInfo> jobs) : ITableSource
    {
        private static readonly string[] Names = ["Id", "Name", "State", "Type", "HasMoreData", "Start", "End", "Command"];

        public string[] ColumnNames => Names;

        public int Columns => Names.Length;

        public int Rows => jobs.Count;

        public object this[int row, int col]
        {
            get
            {
                var job = jobs[row];
                return col switch
                {
                    0 => job.Id,
                    1 => job.Name,
                    2 => job.State,
                    3 => job.Type,
                    4 => job.HasMoreData,
                    5 => job.Start,
                    6 => job.End,
                    _ => job.Command,
                };
            }
        }
    }
}
