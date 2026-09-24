using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Windows;

/// <summary>Windows Update panel (Alt+U): status, check, grouped checkbox list, details, install, history.</summary>
public sealed class UpdatesPanel : WindowsPanelBase
{
    private readonly UpdatesView? _updates;
    private readonly UpdatesHistoryView? _history;

    public UpdatesPanel(PanelContext context)
        : base(context, "Windows Update")
    {
        var service = Service<IWindowsUpdateService>();
        if (service is not { IsSupported: true })
        {
            Body.Add(new Label { X = 1, Y = 1, Text = "Windows Update is only available on Windows." });
            return;
        }

        _updates = new UpdatesView(this, service) { Title = "Available" };
        _history = new UpdatesHistoryView(this, service) { Title = "History" };
        var tabs = new Tabs { Width = Dim.Fill(), Height = Dim.Fill() };
        tabs.Add(_updates, _history);
        Body.Add(tabs);

        AddHint(Key.F5, "Check", _updates.Check);
        AddHint(Key.F9, "Install selected", _updates.InstallSelected);
        AddHint(Key.F6, "History", _history.Start);
    }

    internal UpdatesView? Updates => _updates;

    internal UpdatesHistoryView? History => _history;

    protected override void Opened()
    {
        _updates?.Start();
        _history?.Start();
    }
}
