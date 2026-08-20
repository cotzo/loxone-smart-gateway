using loxone.smart.gateway.Api.Irrigation;
using Microsoft.AspNetCore.Mvc;

namespace loxone.smart.gateway.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class IrrigationController(
    IrrigationService service,
    WeatherIngestionService weatherIngestion) : ControllerBase
{
    [HttpGet("weather/{field}/{value:double}")]
    public async Task<IActionResult> SetWeatherValue(string field, double value, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(value)) return BadRequest("Weather value must be a finite number.");
        var updated = await weatherIngestion.SetAsync(field, value, cancellationToken);
        return updated ? Ok() : BadRequest("Unknown weather field. Use temperature, humidity, pressure, wind, light, or rainfall.");
    }

    [HttpGet("weather/commit")]
    public async Task<IActionResult> CommitWeather(CancellationToken cancellationToken)
    {
        var committed = await weatherIngestion.CommitAsync(cancellationToken);
        return committed ? Ok() : BadRequest("Weather sample is incomplete. Set all six weather values before commit.");
    }

    [HttpGet]
    public Task<IrrigationResult> Get(CancellationToken cancellationToken) => service.CalculateAsync(cancellationToken);

    [HttpGet("irrigate")]
    public async Task<double> GetIrrigate(CancellationToken cancellationToken) => (await service.CalculateAsync(cancellationToken)).Irrigate ? 1 : 0;

    [HttpGet("data-complete")]
    public async Task<double> GetDataComplete(CancellationToken cancellationToken) => (await service.CalculateAsync(cancellationToken)).LocalDataComplete ? 1 : 0;

    [HttpGet("et0")]
    public async Task<double> GetEt0(CancellationToken cancellationToken) => (await service.CalculateAsync(cancellationToken)).Et0Observed24hMm;

    [HttpGet("rain24h")]
    public async Task<double> GetRain24h(CancellationToken cancellationToken) => (await service.CalculateAsync(cancellationToken)).Rain24hMm;

    [HttpGet("rain72h")]
    public async Task<double> GetRain72h(CancellationToken cancellationToken) => (await service.CalculateAsync(cancellationToken)).Rain72hMm;

    [HttpGet("forecast-rain")]
    public async Task<double> GetForecastRain(CancellationToken cancellationToken) => (await service.CalculateAsync(cancellationToken)).ForecastRainMm;

    [HttpGet("deficit")]
    public async Task<double> GetDeficit(CancellationToken cancellationToken) => (await service.CalculateAsync(cancellationToken)).WaterDeficitMm;

    [HttpGet("zone/{id}")]
    public async Task<ActionResult<int>> GetZoneRuntime(string id, CancellationToken cancellationToken)
    {
        var result = await service.CalculateAsync(cancellationToken);
        var zone = result.Zones.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        return zone is null ? NotFound() : Ok(zone.RuntimeSeconds);
    }

    // Loxone calls this after a zone actually ran. The delivered depth is subtracted from
    // that zone's persistent soil-water deficit using its configured application rate.
    [HttpGet("zone/{id}/applied/{runtimeSeconds:int}")]
    public async Task<IActionResult> RecordAppliedWater(string id, int runtimeSeconds, CancellationToken cancellationToken)
    {
        var recorded = await service.RecordIrrigationAsync(id, runtimeSeconds, cancellationToken);
        return recorded ? Ok() : BadRequest("Unknown zone, invalid application rate, or runtime must be greater than zero.");
    }
}
