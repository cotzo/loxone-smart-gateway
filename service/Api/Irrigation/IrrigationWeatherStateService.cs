using System.Globalization;
using loxone.smart.gateway.Api.Gateway;
using Serilog;

namespace loxone.smart.gateway.Api.Irrigation;

public sealed class IrrigationWeatherStateService(GatewayStateStore store, WeatherIngestionService ingestion) : BackgroundService
{
    private static readonly string[] RequiredFields = ["temperature", "humidity", "pressure", "wind", "light", "rainfall"];
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(5);
    private readonly object _sync = new();
    private readonly Dictionary<string, (string ServerId, string Uuid, double Value, DateTimeOffset ReceivedAt)> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _healthyServers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _commitGate = new(1, 1);
    private DateTimeOffset? _lastCommittedAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await CommitIfDueAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { Log.Error(ex, "Failed to persist scheduled Loxone weather observation"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

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

        lock (_sync)
        {
            _healthyServers[serverId] = DateTimeOffset.UtcNow;
            foreach (var mapping in matches)
                if (RequiredFields.Contains(mapping.Field, StringComparer.OrdinalIgnoreCase))
                    _latest[mapping.Field] = (mapping.ServerId, Normalize(mapping.StateUuid), value, DateTimeOffset.UtcNow);
        }

        // Save the first complete snapshot immediately. Later sensor changes only refresh
        // the in-memory readings; the timer persists a combined snapshot every five minutes.
        try { await CommitIfDueAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log.Error(ex, "Failed to persist mapped Loxone weather observation"); }
    }

    public void ReportSourceStatus(string serverId, string status)
    {
        lock (_sync)
        {
            if (status.Contains("Connected", StringComparison.OrdinalIgnoreCase) &&
                !status.Contains("Disconnected", StringComparison.OrdinalIgnoreCase))
                _healthyServers[serverId] = DateTimeOffset.UtcNow;
            else
                _healthyServers.Remove(serverId);
        }
    }

    public IrrigationWeatherSourceStatus GetSourceStatus()
    {
        var mappings = store.GetSnapshot().Settings.Irrigation.WeatherStateMappings;
        var timeout = TimeSpan.FromMinutes(Math.Max(1, store.GetSnapshot().Settings.Irrigation.WeatherSourceHealthTimeoutMinutes));
        lock (_sync)
        {
            var fields = RequiredFields.ToDictionary(
                field => field,
                field => _latest.TryGetValue(field, out var value) ? (DateTimeOffset?)value.ReceivedAt : null,
                StringComparer.OrdinalIgnoreCase);
            var configured = RequiredFields.All(field => mappings.Any(x => x.Field.Equals(field, StringComparison.OrdinalIgnoreCase)));
            var sourceIds = mappings.Where(x => RequiredFields.Contains(x.Field, StringComparer.OrdinalIgnoreCase))
                .Select(x => x.ServerId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var now = DateTimeOffset.UtcNow;
            var healthy = configured && sourceIds.Count > 0 && sourceIds.All(id =>
                _healthyServers.TryGetValue(id, out var lastHeartbeat) && now - lastHeartbeat <= timeout);
            return new IrrigationWeatherSourceStatus(healthy,
                !configured ? "Weather state mappings are incomplete." : healthy
                    ? "All mapped Loxone weather sources are connected and healthy."
                    : "At least one mapped Loxone weather source is disconnected or has not reported healthy recently.",
                fields);
        }
    }

    private async Task CommitIfDueAsync(CancellationToken cancellationToken)
    {
        await _commitGate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, double>? sample;
            DateTimeOffset sampleTime;
            DateTimeOffset? previousCommit;
            var mappings = store.GetSnapshot().Settings.Irrigation.WeatherStateMappings;
            lock (_sync)
            {
                sampleTime = DateTimeOffset.UtcNow;
                if (_lastCommittedAt is not null && sampleTime - _lastCommittedAt.Value < SampleInterval)
                    return;

                var configured = RequiredFields.All(field => mappings.Any(x => x.Field.Equals(field, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(x.ServerId) && !string.IsNullOrWhiteSpace(x.StateUuid)));
                var readingsValid = configured && RequiredFields.All(field => _latest.TryGetValue(field, out var reading) &&
                    mappings.Any(mapping => mapping.Field.Equals(field, StringComparison.OrdinalIgnoreCase) &&
                        mapping.ServerId.Equals(reading.ServerId, StringComparison.OrdinalIgnoreCase) &&
                        Normalize(mapping.StateUuid).Equals(reading.Uuid, StringComparison.OrdinalIgnoreCase)));
                if (!readingsValid) return;

                var sourceIds = mappings.Where(x => RequiredFields.Contains(x.Field, StringComparer.OrdinalIgnoreCase))
                    .Select(x => x.ServerId).Distinct(StringComparer.OrdinalIgnoreCase);
                var timeout = TimeSpan.FromMinutes(Math.Max(1, store.GetSnapshot().Settings.Irrigation.WeatherSourceHealthTimeoutMinutes));
                if (sourceIds.Any(id => !_healthyServers.TryGetValue(id, out var heartbeat) || sampleTime - heartbeat > timeout))
                    return;

                sample = RequiredFields.ToDictionary(field => field, field => _latest[field].Value, StringComparer.OrdinalIgnoreCase);
                previousCommit = _lastCommittedAt;
                _lastCommittedAt = sampleTime;
            }

            var observation = new WeatherObservation(sample["temperature"], sample["humidity"], sample["pressure"],
                sample["wind"], sample["light"], sample["rainfall"]);
            try
            {
                await ingestion.CommitMappedAsync(observation, cancellationToken);
            }
            catch
            {
                lock (_sync) _lastCommittedAt = previousCommit;
                throw;
            }
            Log.Information("Committed mapped Loxone weather observation: temperature={Temperature}°C, humidity={Humidity}%, pressure={Pressure} hPa, wind={Wind} km/h, light={Light} lux, rainfall counter={Rainfall} mm",
                observation.TemperatureC, observation.HumidityPct, observation.PressureHpa, observation.WindSpeedKmh, observation.LightLux, observation.RainfallMm);
        }
        finally { _commitGate.Release(); }
    }

    private static string Normalize(string value) => value.Replace("-", string.Empty, StringComparison.Ordinal).Trim();
}
