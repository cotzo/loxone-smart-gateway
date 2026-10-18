using System.Globalization;
using loxone.smart.gateway.Api.Gateway;
using Serilog;

namespace loxone.smart.gateway.Api.Irrigation;

public sealed class IrrigationWeatherStateService(GatewayStateStore store, WeatherIngestionService ingestion)
{
    private static readonly string[] RequiredFields = ["temperature", "humidity", "pressure", "wind", "light", "rainfall"];
    private readonly object _sync = new();
    private readonly Dictionary<string, (string ServerId, string Uuid, double Value, DateTimeOffset ReceivedAt)> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _commitGate = new(1, 1);

    public async Task ObserveStateAsync(string serverId, string uuid, string raw, CancellationToken cancellationToken)
    {
        var mappings = store.GetSnapshot().Settings.Irrigation.WeatherStateMappings;
        var matches = mappings.Where(x => x.ServerId.Equals(serverId, StringComparison.OrdinalIgnoreCase) &&
            Normalize(x.StateUuid).Equals(Normalize(uuid), StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            Log.Warning("Ignoring non-numeric Loxone weather state {Uuid} for {Fields}: {Value}", uuid, string.Join(",", matches.Select(x => x.Field)), raw);
            return;
        }

        Dictionary<string, double>? sample = null;
        lock (_sync)
        {
            foreach (var mapping in matches)
                if (RequiredFields.Contains(mapping.Field, StringComparer.OrdinalIgnoreCase))
                    _latest[mapping.Field] = (mapping.ServerId, Normalize(mapping.StateUuid), value, DateTimeOffset.UtcNow);

            var configured = RequiredFields.All(field => mappings.Any(x => x.Field.Equals(field, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(x.ServerId) && !string.IsNullOrWhiteSpace(x.StateUuid)));
            if (configured && RequiredFields.All(field => _latest.TryGetValue(field, out var reading) && DateTimeOffset.UtcNow - reading.ReceivedAt <= TimeSpan.FromMinutes(15) &&
                mappings.Any(mapping => mapping.Field.Equals(field, StringComparison.OrdinalIgnoreCase) &&
                    mapping.ServerId.Equals(reading.ServerId, StringComparison.OrdinalIgnoreCase) && Normalize(mapping.StateUuid).Equals(reading.Uuid, StringComparison.OrdinalIgnoreCase))))
                sample = RequiredFields.ToDictionary(field => field, field => _latest[field].Value, StringComparer.OrdinalIgnoreCase);
        }

        if (sample is null) return;
        await _commitGate.WaitAsync(cancellationToken);
        try
        {
            var observation = new WeatherObservation(sample["temperature"], sample["humidity"], sample["pressure"],
                sample["wind"], sample["light"], sample["rainfall"]);
            await ingestion.CommitMappedAsync(observation, cancellationToken);
            Log.Information("Committed mapped Loxone weather observation from {ServerId}: temperature={Temperature}°C, humidity={Humidity}%, pressure={Pressure} hPa, wind={Wind} km/h, light={Light} lux, rainfall counter={Rainfall} mm", serverId,
                observation.TemperatureC, observation.HumidityPct, observation.PressureHpa, observation.WindSpeedKmh, observation.LightLux, observation.RainfallMm);
        }
        finally { _commitGate.Release(); }
    }

    private static string Normalize(string value) => value.Replace("-", string.Empty, StringComparison.Ordinal).Trim();
}
