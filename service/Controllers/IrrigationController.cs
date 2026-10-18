using loxone.smart.gateway.Api.Irrigation;
using Microsoft.AspNetCore.Mvc;

namespace loxone.smart.gateway.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class IrrigationController(
    IrrigationService service,
    MowingWetnessService mowingWetness) : ControllerBase
{
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

    [HttpGet("mowing")]
    public Task<MowingStatus> GetMowingStatus(CancellationToken cancellationToken) =>
        mowingWetness.GetStatusAsync(cancellationToken);

    [HttpGet("mowing-allowed")]
    public async Task<double> GetMowingAllowed(CancellationToken cancellationToken) =>
        (await mowingWetness.GetStatusAsync(cancellationToken)).MowingAllowed ? 1 : 0;

    [HttpGet("lawn-wetness")]
    public async Task<double> GetLawnWetness(CancellationToken cancellationToken) =>
        (await mowingWetness.GetStatusAsync(cancellationToken)).LawnWetnessMm;

    [HttpGet("zone/{id}")]
    public async Task<ActionResult<int>> GetZoneRuntime(string id, CancellationToken cancellationToken)
    {
        var result = await service.CalculateAsync(cancellationToken);
        var zone = result.Zones.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        return zone is null ? NotFound() : Ok(zone.RuntimeSeconds);
    }

    [HttpGet("history")]
    public Task<IReadOnlyList<IrrigationRun>> GetHistory(CancellationToken cancellationToken) => service.GetIrrigationRunsAsync(cancellationToken);
}
