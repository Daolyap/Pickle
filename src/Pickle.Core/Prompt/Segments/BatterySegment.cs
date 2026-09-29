using System.Globalization;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Prompt.Segments;

/// <summary>
/// Battery charge, "57%" (with "↑" while charging). Hidden without a battery and on AC power at or above option
/// <c>hideAbove</c> (default 95); at or below option <c>low</c> (default 15) it turns red.
/// </summary>
public sealed class BatterySegment(SegmentEnvironment environment) : IPromptSegment
{
    public string Type => "battery";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken)
    {
        if (environment.GetBattery() is not { } battery || Format(battery, style.OptionInt("hideAbove", 95)) is not { } text)
        {
            return SegmentText.Hide();
        }

        if (battery.Percent <= style.OptionInt("low", 15) && battery.Charging != true)
        {
            // Blocks turn red; plain text is drawn red.
            return ValueTask.FromResult<PromptSegmentOutput?>(PickleColor.Parse(style.Background) is not null
                ? new PromptSegmentOutput(text, "#FFFFFF", "red")
                : new PromptSegmentOutput(text, "red"));
        }

        return SegmentText.Show(text);
    }

    public static string? Format(BatteryStatus battery, int hideAbove)
    {
        if (battery.Percent is not { } percent || (battery.OnAcPower && percent >= hideAbove))
        {
            return null;
        }

        return percent.ToString(CultureInfo.InvariantCulture) + "%" + (battery.Charging == true ? "↑" : string.Empty);
    }
}
