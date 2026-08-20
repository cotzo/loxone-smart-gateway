using System.Text.Json;
using Serilog;

namespace loxone.smart.gateway.Api.Irrigation;

public sealed class WeatherIngestionService
{
    private readonly IrrigationService _irrigationService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PendingWeather _pending = new();

    public WeatherIngestionService(IrrigationService irrigationService, IConfiguration configuration)
    {
        _irrigationService = irrigationService;
        var options = configuration.GetSection("Api:IrrigationConfiguration").Get<IrrigationConfiguration>() ?? new();
        HydratePending(options.StateFile);
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

        await _irrigationService.AddObservationAsync(observation, cancellationToken);
        return true;
    }

    private void HydratePending(string stateFile)
    {
        try
        {
            if (!File.Exists(stateFile))
                return;

            var state = JsonSerializer.Deserialize<IrrigationState>(File.ReadAllText(stateFile));
            var latest = state?.Observations
                .Where(x => x.Timestamp is not null)
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();

            if (latest is null)
                return;

            _pending.TemperatureC = latest.TemperatureC;
            _pending.HumidityPct = latest.HumidityPct;
            _pending.PressureHpa = latest.PressureHpa;
            _pending.WindSpeedKmh = latest.WindSpeedKmh;
            _pending.LightLux = latest.LightLux;
            _pending.RainfallMm = latest.RainfallMm;

            Log.Information("Hydrated staged irrigation weather values from persisted observation at {timestamp}", latest.Timestamp);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unable to hydrate staged irrigation weather values from {file}", stateFile);
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

        public bool IsComplete =>
            TemperatureC.HasValue &&
            HumidityPct.HasValue &&
            PressureHpa.HasValue &&
            WindSpeedKmh.HasValue &&
            LightLux.HasValue &&
            RainfallMm.HasValue;
    }
}
