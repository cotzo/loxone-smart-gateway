using loxone.smart.gateway.Api.Irrigation;
using Microsoft.AspNetCore.Mvc;

namespace loxone.smart.gateway.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class IrrigationController(
    IrrigationService service,
    WeatherIngestionService weatherIngestion,
    IrrigationRunTracker runTracker) : ControllerBase
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

    [HttpGet("zone/{id}/start")]
    public IActionResult StartRun(string id, [FromQuery] string type = "Irrigation") =>
        runTracker.Start(id, type) ? Ok() : BadRequest("Type must be Irrigation, Rinse, or Manual.");

    [HttpGet("zone/{id}/stop")]
    public async Task<IActionResult> StopRun(string id, CancellationToken cancellationToken) =>
        await runTracker.StopAsync(id, cancellationToken) ? Ok() : BadRequest("Unknown zone or invalid application rate.");

    [HttpGet("zone/{id}/applied/{runtimeSeconds:int}/{eventId}")]
    public async Task<IActionResult> RecordAppliedWater(string id, int runtimeSeconds, string eventId,
        [FromQuery] string type = "Irrigation", CancellationToken cancellationToken = default)
    {
        var recorded = await service.RecordIrrigationAsync(id, runtimeSeconds, eventId, type, cancellationToken);
        return recorded ? Ok() : BadRequest("Unknown zone, invalid application rate/runtime/event id, or type must be Irrigation, Rinse, or Manual.");
    }

    [HttpGet("history")]
    public Task<IReadOnlyList<IrrigationRun>> GetHistory(CancellationToken cancellationToken) => service.GetIrrigationRunsAsync(cancellationToken);
}
