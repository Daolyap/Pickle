using Pickle.Abstractions.Services;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Windows;

internal enum NewTaskTrigger
{
    Daily,
    Weekly,
    Monthly,
    Once,
    Every,
    AtLogon,
    AtStartup,
    OnIdle,
}

/// <summary>What the New task dialog collected.</summary>
internal sealed record NewTaskValues(
    string Name,
    NewTaskTrigger Trigger,
    string Time,
    string Days,
    bool PickleCommand,
    string Command,
    string Arguments,
    bool Elevated)
{
    /// <summary>The friendly schedule text understood by <see cref="ITaskSchedulerService.ParseSchedule"/>.</summary>
    public string ScheduleText => Trigger switch
    {
        NewTaskTrigger.Daily => $"daily {Time}".Trim(),
        NewTaskTrigger.Weekly => $"weekly {Days} {Time}".Trim(),
        NewTaskTrigger.Monthly => $"monthly {Days} {Time}".Trim(),
        NewTaskTrigger.Once => $"once {Time}".Trim(),
        NewTaskTrigger.Every => $"every {Days}".Trim(),
        NewTaskTrigger.AtLogon => "at logon",
        NewTaskTrigger.AtStartup => "at startup",
        _ => "on idle",
    };

    /// <summary>Builds the definition (throws <see cref="FormatException"/>/<see cref="ArgumentException"/> with a user-facing message).</summary>
    public ScheduledTaskDefinition ToDefinition(ITaskSchedulerService scheduler, string? workingDirectory)
    {
        var name = Name.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("Give the task a name.");
        }

        if (Command.Trim().Length == 0)
        {
            throw new ArgumentException(PickleCommand ? "Enter the Pickle command to run." : "Enter the program to run.");
        }

        var trigger = scheduler.ParseSchedule(ScheduleText);
        var cwd = workingDirectory is not null && Path.IsPathFullyQualified(workingDirectory) ? workingDirectory : null;
        var action = PickleCommand
            ? new TaskActionSpec(Environment.ProcessPath ?? "pickle.exe", "-NoLogo -c " + WindowsPanelBase.QuoteArgument(Command.Trim()), cwd)
            : new TaskActionSpec(Command.Trim(), Arguments.Trim().Length == 0 ? null : Arguments.Trim(), cwd);
        return new ScheduledTaskDefinition(name, trigger, action, @"\Pickle", "Created by Pickle", RunElevated: Elevated || Trigger == NewTaskTrigger.AtStartup);
    }
}

/// <summary>Modal form for a new scheduled task (name, trigger kind + time/days, Pickle command or program, elevated).</summary>
internal sealed class NewTaskDialog : Dialog
{
    private static readonly string[] TriggerLabels = ["Daily", "Weekly", "Monthly", "Once", "Every", "At logon", "At startup", "On idle"];

    private readonly TextField _name = new() { X = 16, Y = 0, Width = Dim.Fill(1) };
    private readonly OptionSelector _trigger = new() { X = 16, Y = 1, Labels = TriggerLabels, Orientation = Orientation.Horizontal, Value = 0 };
    private readonly TextField _time = new() { X = 16, Y = 3, Width = 20, Text = "09:00" };
    private readonly TextField _days = new() { X = 16, Y = 4, Width = 30, Text = "mon,fri" };
    private readonly OptionSelector _actionKind = new() { X = 16, Y = 6, Labels = ["Pickle command", "Program"], Orientation = Orientation.Horizontal, Value = 0 };
    private readonly TextField _command = new() { X = 16, Y = 7, Width = Dim.Fill(1) };
    private readonly TextField _arguments = new() { X = 16, Y = 8, Width = Dim.Fill(1) };
    private readonly CheckBox _elevated = new() { X = 16, Y = 10, Text = "Run with highest privileges (UAC prompt to create)" };

    public NewTaskDialog()
    {
        Title = "New scheduled task";
        Width = Dim.Percent(80);
        Height = 17;
        Add(
            new Label { Text = "Name:", Y = 0 }, _name,
            new Label { Text = "Trigger:", Y = 1 }, _trigger,
            new Label { Text = "Time / date:", Y = 3 }, _time,
            new Label { Text = "HH:mm, or yyyy-MM-dd HH:mm for Once", X = Pos.Right(_time) + 1, Y = 3 },
            new Label { Text = "Days / every:", Y = 4 }, _days,
            new Label { Text = "mon,fri · 1,15 · 30m / 2h", X = Pos.Right(_days) + 1, Y = 4 },
            new Label { Text = "Action:", Y = 6 }, _actionKind,
            new Label { Text = "Command/program:", Y = 7 }, _command,
            new Label { Text = "Arguments:", Y = 8 }, _arguments,
            _elevated);

        var cancel = new Button { Text = "_Cancel" };
        var create = new Button { Text = "C_reate", IsDefault = true };
        cancel.Accepting += (_, e) =>
        {
            Confirmed = false;
            RequestStop();
            e.Handled = true;
        };
        create.Accepting += (_, e) =>
        {
            Confirmed = true;
            RequestStop();
            e.Handled = true;
        };
        AddButton(cancel);
        AddButton(create);
    }

    public bool Confirmed { get; private set; }

    public NewTaskValues Values => new(
        _name.Text,
        (NewTaskTrigger)(_trigger.Value ?? 0),
        _time.Text,
        _days.Text,
        (_actionKind.Value ?? 0) == 0,
        _command.Text,
        _arguments.Text,
        _elevated.Value == CheckState.Checked);
}
