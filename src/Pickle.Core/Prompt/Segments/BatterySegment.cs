using System.Globalization;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Prompt.Segments;

/// <summary>
/// Battery charge, "57%" (with "↑" while charging). Hidden without a battery and on AC power at or above option
/// <c>hideAbove</c> (default 95); at or below option <c>low</c> (default 15) it turns red. The defaults come from the
/// <c>battery</c> section of config.json (<c>battery.hideAbove</c>, <c>battery.low</c>, <c>battery.chargingMarker</c>);
/// options in a theme's segment win.
/// </summary>
public sealed class BatterySegment(SegmentEnvironment environment) : IPromptSegment
{
    public string Type => "battery";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken)
    {
        var settings = context.Pickle?.Config.Current.Battery ?? new BatterySettings();
        if (environment.GetBattery() is not { } battery
            || Format(battery, style.OptionInt("hideAbove", settings.HideAbove), style.Options.TryGetValue("chargingMarker", out var marker) ? marker : settings.ChargingMarker) is not { } text)
        {
            return SegmentText.Hide();
        }

        if (battery.Percent <= style.OptionInt("low", settings.Low) && battery.Charging != true)
        {
            // Blocks turn red; plain text is drawn red.
            return ValueTask.FromResult<PromptSegmentOutput?>(PickleColor.Parse(style.Background) is not null
                ? new PromptSegmentOutput(text, "#FFFFFF", "red")
                : new PromptSegmentOutput(text, "red"));
        }

        return SegmentText.Show(text);
    }

    public static string? Format(BatteryStatus battery, int hideAbove, string chargingMarker = "↑")
    {
        if (battery.Percent is not { } percent || (battery.OnAcPower && percent >= hideAbove))
        {
            return null;
        }

        return percent.ToString(CultureInfo.InvariantCulture) + "%" + (battery.Charging == true ? chargingMarker : string.Empty);
    }
}
