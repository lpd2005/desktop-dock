using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed class WeatherService : IDisposable
{
    private readonly HttpClient client;
    private readonly Dictionary<string, (double Latitude, double Longitude, string Name)> locations = new(StringComparer.OrdinalIgnoreCase);
    public WeatherService(HttpMessageHandler? handler = null)
    {
        client = handler is null ? new() : new(handler);
        client.Timeout = TimeSpan.FromSeconds(12);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopDock/0.1");
    }
    public async Task<WeatherSnapshot> Fetch(string city, CancellationToken cancellation)
    {
        if (!locations.TryGetValue(city, out var point))
        {
            // Chinese short names can resolve to a smaller namesake town. Try
            // the municipal name first, then retain the original as a fallback.
            bool chineseCity = city.Length is >= 2 and <= 6 && city.All(c => c is >= '\u4e00' and <= '\u9fff') && !city.EndsWith('市') && !city.EndsWith('县') && !city.EndsWith('区');
            string[] queries = chineseCity ? [city + "市", city] : [city];
            JsonElement? chosen = null;
            foreach (string query in queries)
            {
                string json = await client.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(query) + "&count=5&language=zh&format=json", cancellation);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) continue;
                chosen = results.EnumerateArray().OrderByDescending(x => x.TryGetProperty("population", out var population) && population.TryGetInt64(out long count) ? count : 0).First().Clone();
                break;
            }
            if (chosen is not { } first) throw new HttpRequestException("City not found.");
            string name = first.GetProperty("name").GetString() ?? city;
            if (first.TryGetProperty("admin1", out var region) && !string.IsNullOrEmpty(region.GetString()) && region.GetString() != name) name += " · " + region.GetString();
            point = (first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble(), name);
            if (!double.IsFinite(point.Latitude) || !double.IsFinite(point.Longitude) || Math.Abs(point.Latitude) > 90 || Math.Abs(point.Longitude) > 180) throw new JsonException("Invalid location.");
            locations[city] = point;
        }
        string latitude = point.Latitude.ToString(CultureInfo.InvariantCulture), longitude = point.Longitude.ToString(CultureInfo.InvariantCulture);
        string response = await client.GetStringAsync($"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,wind_speed_10m&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto", cancellation);
        return WeatherSnapshot.Parse(city, response, DateTimeOffset.UtcNow) with { Location = point.Name };
    }
    public void Dispose() => client.Dispose();
}
