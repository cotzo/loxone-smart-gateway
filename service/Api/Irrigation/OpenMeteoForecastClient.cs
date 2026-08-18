using System.Text.Json.Serialization;

namespace loxone.smart.gateway.Api.Irrigation;

public sealed class OpenMeteoForecastClient(HttpClient httpClient, IConfiguration configuration)
{
    private readonly IrrigationConfiguration _options =
        configuration.GetSection("Api:IrrigationConfiguration").Get<IrrigationConfiguration>() ?? new();

    public async Task<ForecastSummary> GetForecastAsync(CancellationToken cancellationToken)
    {
        var hours = Math.Clamp(_options.ForecastHours, 1, 168);
        var url = $"v1/forecast?latitude={_options.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                  $"&longitude={_options.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                  $"&hourly=precipitation,et0_fao_evapotranspiration&forecast_hours={hours}&timezone=auto";

        var response = await httpClient.GetFromJsonAsync<OpenMeteoResponse>(url, cancellationToken)
                       ?? throw new ApplicationException("Open-Meteo returned an empty response");

        var count = Math.Min(hours, Math.Min(response.Hourly.Precipitation.Count, response.Hourly.Et0.Count));
        return new ForecastSummary(
            response.Hourly.Precipitation.Take(count).Sum(),
            response.Hourly.Et0.Take(count).Sum());
    }

    private sealed class OpenMeteoResponse
    {
        [JsonPropertyName("hourly")]
        public HourlyData Hourly { get; set; } = new();
    }

    private sealed class HourlyData
    {
        [JsonPropertyName("precipitation")]
        public List<double> Precipitation { get; set; } = [];

        [JsonPropertyName("et0_fao_evapotranspiration")]
        public List<double> Et0 { get; set; } = [];
    }
}

public sealed record ForecastSummary(double RainMm, double Et0Mm);
