using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pickle.Abstractions;

namespace Pickle.Core.Prompt.Segments.Live;

/// <summary>What the weather segment shows: one reading, cached by the background job.</summary>
public sealed record WeatherReading(double Temperature, double? FeelsLike, int Code, bool IsDay, string Unit, double? Wind, string WindUnit, string? City);

public interface IWeatherProvider
{
    Task<WeatherReading?> GetAsync(WeatherSettings settings, CancellationToken cancellationToken);
}

/// <summary>Turns a reading into the segment's text (<c>{icon} {temp}{unit}</c> by default).</summary>
public static class WeatherFormat
{
    public static string Render(WeatherReading reading, string format)
    {
        var template = string.IsNullOrWhiteSpace(format) ? "{icon} {temp}{unit}" : format;
        return template
            .Replace("{icon}", Icon(reading.Code, reading.IsDay), StringComparison.Ordinal)
            .Replace("{temp}", Round(reading.Temperature), StringComparison.Ordinal)
            .Replace("{unit}", "°" + reading.Unit, StringComparison.Ordinal)
            .Replace("{feels}", reading.FeelsLike is { } feels ? Round(feels) : Round(reading.Temperature), StringComparison.Ordinal)
            .Replace("{condition}", Condition(reading.Code), StringComparison.Ordinal)
            .Replace("{wind}", reading.Wind is { } wind ? Round(wind) + " " + reading.WindUnit : string.Empty, StringComparison.Ordinal)
            .Replace("{city}", reading.City ?? string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    public static string Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>WMO weather interpretation codes as used by Open-Meteo.</summary>
    public static string Condition(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        >= 51 and <= 57 => "Drizzle",
        >= 61 and <= 67 => "Rain",
        >= 71 and <= 77 => "Snow",
        >= 80 and <= 82 => "Showers",
        85 or 86 => "Snow showers",
        >= 95 and <= 99 => "Thunderstorm",
        _ => "Unknown",
    };

    public static string Icon(int code, bool isDay) => code switch
    {
        0 or 1 => isDay ? "☀" : "☾",
        2 => isDay ? "⛅" : "☁",
        3 => "☁",
        45 or 48 => "🌫",
        >= 51 and <= 57 => "🌦",
        >= 61 and <= 67 or >= 80 and <= 82 => "🌧",
        >= 71 and <= 77 or 85 or 86 => "❄",
        >= 95 and <= 99 => "⛈",
        _ => "·",
    };
}

/// <summary>Open-Meteo (geocoding + forecast; no key) with ipwho.is for "auto". Everything it learns about a place is cached in memory.</summary>
public sealed partial class OpenMeteoProvider(HttpClient http) : IWeatherProvider
{
    private readonly Dictionary<string, (double Latitude, double Longitude, string? City)> _places = new(StringComparer.OrdinalIgnoreCase);

    public async Task<WeatherReading?> GetAsync(WeatherSettings settings, CancellationToken cancellationToken)
    {
        var place = await ResolveAsync(settings.Location.Trim(), cancellationToken).ConfigureAwait(false);
        if (place is null)
        {
            return null;
        }

        var imperial = IsImperial(settings.Units);
        var url = string.Create(CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={place.Value.Latitude:0.####}&longitude={place.Value.Longitude:0.####}&current=temperature_2m,apparent_temperature,is_day,weather_code,wind_speed_10m&temperature_unit={(imperial ? "fahrenheit" : "celsius")}&wind_speed_unit={(imperial ? "mph" : "kmh")}");
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseForecast(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), imperial, place.Value.City);
    }

    public static bool IsImperial(string units) =>
        units.Equals("imperial", StringComparison.OrdinalIgnoreCase)
        || (!units.Equals("metric", StringComparison.OrdinalIgnoreCase) && !RegionInfo.CurrentRegion.IsMetric);

    public static WeatherReading? ParseForecast(string json, bool imperial, string? city)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("current", out var current) || !current.TryGetProperty("temperature_2m", out var temperature))
        {
            return null;
        }

        double? Number(string name) => current.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
        return new WeatherReading(
            temperature.GetDouble(),
            Number("apparent_temperature"),
            (int)(Number("weather_code") ?? -1),
            (Number("is_day") ?? 1) != 0,
            imperial ? "F" : "C",
            Number("wind_speed_10m"),
            imperial ? "mph" : "km/h",
            city);
    }

    public static (double Latitude, double Longitude)? ParseCoordinates(string text)
    {
        var match = Coordinates().Match(text);
        return match.Success
            && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var lat) && lat is >= -90 and <= 90
            && double.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var lon) && lon is >= -180 and <= 180
            ? (lat, lon)
            : null;
    }

    private async Task<(double Latitude, double Longitude, string? City)?> ResolveAsync(string location, CancellationToken cancellationToken)
    {
        if (location.Length == 0)
        {
            return null;
        }

        if (_places.TryGetValue(location, out var cached))
        {
            return cached;
        }

        if (ParseCoordinates(location) is { } coordinates)
        {
            return _places[location] = (coordinates.Latitude, coordinates.Longitude, null);
        }

        (double, double, string?)? found = null;
        if (location.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            using var response = await http.GetAsync("https://ipwho.is/", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            if (root.TryGetProperty("latitude", out var lat) && root.TryGetProperty("longitude", out var lon))
            {
                found = (lat.GetDouble(), lon.GetDouble(), root.TryGetProperty("city", out var city) ? city.GetString() : null);
            }
        }
        else
        {
            var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&name=" + Uri.EscapeDataString(location.Split(',')[0].Trim());
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var geocoded = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
            if (geocoded.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
                var first = results[0];
                found = (first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble(), first.TryGetProperty("name", out var name) ? name.GetString() : null);
            }
        }

        if (found is { } place)
        {
            _places[location] = place;
            return place;
        }

        return null;
    }

    [GeneratedRegex(@"^\s*(-?\d{1,3}(?:\.\d+)?)\s*,\s*(-?\d{1,3}(?:\.\d+)?)\s*$")]
    private static partial Regex Coordinates();
}
