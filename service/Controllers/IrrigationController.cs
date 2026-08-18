using loxone.smart.gateway.Api.Irrigation;
using Microsoft.AspNetCore.Mvc;

namespace loxone.smart.gateway.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class IrrigationController(IrrigationService service) : ControllerBase
{
    [HttpPost("weather")]
    public async Task<IActionResult> AddWeather([FromBody] WeatherObservation observation, CancellationToken cancellationToken)
    {
        await service.AddObservationAsync(observation, cancellationToken);
        return Ok();
    }

    [HttpGet]
    public Task<IrrigationResult> Get(CancellationToken cancellationToken) =>
        service.CalculateAsync(cancellationToken);

    // Simple scalar endpoints are intentionally provided for Loxone Virtual HTTP Inputs.
    [HttpGet("irrigate")]
    public async Task<double> GetIrrigate(CancellationToken cancellationToken) =>
        (await service.CalculateAsync(cancellationToken)).Irrigate ? 1 : 0;

    [HttpGet("et0")]
    public async Task<double> GetEt0(CancellationToken cancellationToken) =>
        (await service.CalculateAsync(cancellationToken)).Et0Observed24hMm;

    [HttpGet("rain24h")]
    public async Task<double> GetRain24h(CancellationToken cancellationToken) =>
        (await service.CalculateAsync(cancellationToken)).Rain24hMm;

    [HttpGet("rain72h")]
    public async Task<double> GetRain72h(CancellationToken cancellationToken) =>
        (await service.CalculateAsync(cancellationToken)).Rain72hMm;

    [HttpGet("forecast-rain")]
    public async Task<double> GetForecastRain(CancellationToken cancellationToken) =>
        (await service.CalculateAsync(cancellationToken)).ForecastRainMm;

    [HttpGet("deficit")]
    public async Task<double> GetDeficit(CancellationToken cancellationToken) =>
        (await service.CalculateAsync(cancellationToken)).WaterDeficitMm;

    [HttpGet("zone/{id}")]
    public async Task<ActionResult<int>> GetZoneRuntime(string id, CancellationToken cancellationToken)
    {
        var result = await service.CalculateAsync(cancellationToken);
        var zone = result.Zones.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        return zone is null ? NotFound() : Ok(zone.RuntimeSeconds);
    }
}
