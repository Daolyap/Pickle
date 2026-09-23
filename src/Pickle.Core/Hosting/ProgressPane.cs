using System.Globalization;
using System.Management.Automation;
using System.Text;
using Pickle.Abstractions;

namespace Pickle.Core.Hosting;

/// <summary>
/// Renders Write-Progress as a single bar on the current line. Any other output clears it first, so progress
/// never interleaves with real output. Rendering is throttled to ~20fps.
/// </summary>
internal sealed class ProgressPane
{
    private readonly PickleRuntime _runtime;
    private readonly Dictionary<(long, int), ProgressRecord> _active = [];
    private bool _visible;
    private long _lastRenderTicks;

    public ProgressPane(PickleRuntime runtime) => _runtime = runtime;

    public void Update(long sourceId, ProgressRecord record)
    {
        var key = (sourceId, record.ActivityId);
        if (record.RecordType == ProgressRecordType.Completed)
        {
            _active.Remove(key);
        }
        else
        {
            _active[key] = record;
        }

        if (_active.Count == 0)
        {
            ClearIfVisible();
            return;
        }

        var now = Environment.TickCount64;
        if (_visible && now - _lastRenderTicks < 50)
        {
            return;
        }

        _lastRenderTicks = now;
        Render(_active.Values.Last());
    }

    public void ClearIfVisible()
    {
        if (_visible)
        {
            _runtime.Terminal.Write("\r" + Ansi.ClearToEndOfLine);
            _visible = false;
        }
    }

    public void Reset()
    {
        _active.Clear();
        ClearIfVisible();
    }

    private void Render(ProgressRecord record)
    {
        var width = Math.Max(20, _runtime.Terminal.Width - 1);
        var theme = _runtime.Themes.Current;
        var label = string.IsNullOrEmpty(record.StatusDescription) || record.StatusDescription == "Processing"
            ? record.Activity
            : $"{record.Activity}: {record.StatusDescription}";

        var sb = new StringBuilder("\r");
        var barWidth = Math.Min(30, width / 3);
        if (record.PercentComplete >= 0)
        {
            var filled = (int)Math.Round(barWidth * Math.Clamp(record.PercentComplete, 0, 100) / 100.0);
            sb.Append(Ansi.Style(theme.Ui.Accent)).Append(new string('█', filled))
              .Append(Ansi.Style(theme.Ui.Muted)).Append(new string('░', barWidth - filled))
              .Append(Ansi.Reset)
              .Append(' ')
              .Append(record.PercentComplete.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append("% ");
        }
        else
        {
            sb.Append(Ansi.Style(theme.Ui.Accent)).Append("⋯ ").Append(Ansi.Reset);
        }

        var used = TextWidth.VisibleWidth(sb.ToString());
        sb.Append(TextWidth.Truncate(label, Math.Max(0, width - used)));
        if (record.SecondsRemaining > 0 && TextWidth.VisibleWidth(sb.ToString()) + 12 < width)
        {
            sb.Append(Ansi.Style(theme.Ui.Muted)).Append($" ({TimeSpan.FromSeconds(record.SecondsRemaining):g} left)").Append(Ansi.Reset);
        }

        sb.Append(Ansi.ClearToEndOfLine);
        _runtime.Terminal.Write(sb.ToString());
        _visible = true;
    }
}
