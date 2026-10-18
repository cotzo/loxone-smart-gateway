namespace loxone.smart.gateway.Api.Irrigation;

/// <summary>Persists complete observations assembled from mapped Loxone sensor states.</summary>
public sealed class WeatherIngestionService(IrrigationService irrigationService)
{
    public Task CommitMappedAsync(WeatherObservation observation, CancellationToken cancellationToken) =>
        irrigationService.AddObservationAsync(observation with { Timestamp = DateTimeOffset.UtcNow }, cancellationToken);
}
