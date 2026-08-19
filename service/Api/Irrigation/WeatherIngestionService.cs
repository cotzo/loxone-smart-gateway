namespace loxone.smart.gateway.Api.Irrigation;

public sealed class WeatherIngestionService(IrrigationService irrigationService)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PendingWeather _pending = new();

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

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> CommitAsync(CancellationToken cancellationToken)
    {
        WeatherObservation? observation;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_pending.IsComplete)
                return false;

            observation = new WeatherObservation(
                _pending.TemperatureC!.Value,
                _pending.HumidityPct!.Value,
                _pending.PressureHpa!.Value,
                _pending.WindSpeedKmh!.Value,
                _pending.LightLux!.Value,
                _pending.RainfallMm!.Value);
        }
        finally
        {
            _gate.Release();
        }

        await irrigationService.AddObservationAsync(observation, cancellationToken);
        return true;
    }

    private sealed class PendingWeather
    {
        public double? TemperatureC { get; set; }
        public double? HumidityPct { get; set; }
        public double? PressureHpa { get; set; }
        public double? WindSpeedKmh { get; set; }
        public double? LightLux { get; set; }
        public double? RainfallMm { get; set; }

        public bool IsComplete =>
            TemperatureC.HasValue &&
            HumidityPct.HasValue &&
            PressureHpa.HasValue &&
            WindSpeedKmh.HasValue &&
            LightLux.HasValue &&
            RainfallMm.HasValue;
    }
}
