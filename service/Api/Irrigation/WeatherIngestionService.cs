
namespace loxone.smart.gateway.Api.Irrigation;

public sealed class WeatherIngestionService
{
    private readonly IrrigationService _irrigationService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PendingWeather _pending = new();

    public WeatherIngestionService(IrrigationService irrigationService)
    {
        _irrigationService = irrigationService;
    }

    public async Task<bool> SetAsync(string field, double value, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(value)) return false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            switch (field.ToLowerInvariant())
            {
                case "temperature":
                    _pending.TemperatureC = value;
                    break;
                case "humidity":
                    _pending.HumidityPct = value;
                    break;
                case "pressure":
                    _pending.PressureHpa = value;
                    break;
                case "wind":
                    _pending.WindSpeedKmh = value;
                    break;
                case "light":
                    _pending.LightLux = value;
                    break;
                case "rainfall":
                    _pending.RainfallMm = value;
                    break;
                default:
                    return false;
            }

            _pending.UpdatedAt[field.ToLowerInvariant()] = DateTimeOffset.UtcNow;

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> CommitAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_pending.IsComplete)
                return false;

            var observation = new WeatherObservation(
                _pending.TemperatureC!.Value,
                _pending.HumidityPct!.Value,
                _pending.PressureHpa!.Value,
                _pending.WindSpeedKmh!.Value,
                _pending.LightLux!.Value,
                _pending.RainfallMm!.Value);

            await _irrigationService.AddObservationAsync(observation, cancellationToken);
            _pending.Clear();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task CommitMappedAsync(WeatherObservation observation, CancellationToken cancellationToken) =>
        _irrigationService.AddObservationAsync(observation with { Timestamp = DateTimeOffset.UtcNow }, cancellationToken);

    public async Task<IrrigationWeatherInputState> GetPendingSnapshotAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = new Dictionary<string, double?>
            {
                ["temperature"] = _pending.TemperatureC,
                ["humidity"] = _pending.HumidityPct,
                ["pressure"] = _pending.PressureHpa,
                ["wind"] = _pending.WindSpeedKmh,
                ["light"] = _pending.LightLux,
                ["rainfall"] = _pending.RainfallMm
            };
            return new IrrigationWeatherInputState(values, new Dictionary<string, DateTimeOffset>(_pending.UpdatedAt), _pending.IsComplete);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class PendingWeather
    {
        public double? TemperatureC { get; set; }
        public double? HumidityPct { get; set; }
        public double? PressureHpa { get; set; }
        public double? WindSpeedKmh { get; set; }
        public double? LightLux { get; set; }
        public double? RainfallMm { get; set; }
        public Dictionary<string, DateTimeOffset> UpdatedAt { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsComplete =>
            TemperatureC.HasValue &&
            HumidityPct.HasValue &&
            PressureHpa.HasValue &&
            WindSpeedKmh.HasValue &&
            LightLux.HasValue &&
            RainfallMm.HasValue;

        public void Clear()
        {
            TemperatureC = null;
            HumidityPct = null;
            PressureHpa = null;
            WindSpeedKmh = null;
            LightLux = null;
            RainfallMm = null;
            UpdatedAt.Clear();
        }
    }
}
