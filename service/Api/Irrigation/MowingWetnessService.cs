using System.Text.Json;

namespace loxone.smart.gateway.Api.Irrigation;

public sealed class MowingWetnessService
{
    private readonly IrrigationConfiguration _options;
    private readonly IrrigationRunTracker _runTracker;

    public MowingWetnessService(IConfiguration configuration, IrrigationRunTracker runTracker)
    {
        _options = configuration.GetSection("Api:IrrigationConfiguration").Get<IrrigationConfiguration>() ?? new();
        _runTracker = runTracker;
    }

    public async Task<MowingStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var state = LoadState();
        var observations = state.Observations
            .Where(x => x.Timestamp is not null)
            .OrderBy(x => x.Timestamp)
            .ToList();

        var lawnZoneIds = _options.Zones
            .Where(x => string.Equals(x.Type, "Lawn", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var latestObservation = observations.LastOrDefault()?.Timestamp;
        var weatherDataFresh = latestObservation is not null &&
                               latestObservation >= now.AddMinutes(-Math.Max(1, _options.MowingMaximumWeatherAgeMinutes));

        var lookback = TimeSpan.FromHours(Math.Max(6, _options.MowingWetnessLookbackHours));
        var cutoff = now - lookback;
        var baseline = observations.LastOrDefault(x => x.Timestamp < cutoff);
        var window = new List<WeatherObservation>();
        if (baseline is not null) window.Add(baseline);
        window.AddRange(observations.Where(x => x.Timestamp >= cutoff));

        var lawnRuns = state.IrrigationRuns
            .Where(x => lawnZoneIds.Contains(x.ZoneId) && x.EndedAt >= cutoff && x.EndedAt <= now)
            .OrderBy(x => x.EndedAt)
            .ToList();

        var wetnessMm = CalculateSurfaceWetnessMm(window, lawnRuns, cutoff, now, _options);
        var rainingNowWindow = now.AddMinutes(-Math.Max(1, _options.MowingRainingNowMinutes));
        var rainingNow = RainForPeriod(observations, rainingNowWindow, now) > 0.01;

        var heavyRainWindow = now.AddHours(-Math.Max(1, _options.MowingHeavyRainLockoutHours));
        var rainDuringLockout = RainForPeriod(observations, heavyRainWindow, now);
        var heavyRainLockout = rainDuringLockout >= Math.Max(0, _options.MowingHeavyRainThresholdMm);

        var irrigationRunning = await _runTracker.IsAnyZoneActiveAsync(lawnZoneIds, cancellationToken);
        var threshold = Math.Max(0, _options.MowingAllowedWetnessMm);
        var allowed = weatherDataFresh && wetnessMm <= threshold && !rainingNow && !irrigationRunning && !heavyRainLockout;

        return new MowingStatus(
            allowed,
            Math.Round(wetnessMm, 2),
            Math.Round(threshold, 2),
            weatherDataFresh,
            rainingNow,
            irrigationRunning,
            heavyRainLockout,
            Math.Round(rainDuringLockout, 2),
            now);
    }

    internal static double CalculateSurfaceWetnessMm(
        IReadOnlyList<WeatherObservation> observations,
        IReadOnlyList<IrrigationRun> lawnRuns,
        DateTimeOffset cutoff,
        DateTimeOffset now,
        IrrigationConfiguration options)
    {
        if (observations.Count == 0)
            return lawnRuns.Where(x => x.EndedAt >= cutoff && x.EndedAt <= now).Sum(x => Math.Max(0, x.AppliedMm));

        var surfaceMm = 0.0;
        var runIndex = 0;
        var orderedRuns = lawnRuns.OrderBy(x => x.EndedAt).ToList();

        for (var i = 1; i < observations.Count; i++)
        {
            var previous = observations[i - 1];
            var current = observations[i];
            if (previous.Timestamp is null || current.Timestamp is null) continue;

            var start = previous.Timestamp.Value < cutoff ? cutoff : previous.Timestamp.Value;
            var end = current.Timestamp.Value > now ? now : current.Timestamp.Value;
            if (end <= start) continue;

            var intervalHours = (end - start).TotalHours;
            var maxGapHours = Math.Max(1, options.MowingMaximumDryingGapMinutes) / 60.0;
            if (intervalHours <= maxGapHours)
                surfaceMm = Math.Max(0, surfaceMm - EstimateDryingMm(previous, current, intervalHours, options));

            // The WN90LP rainfall value is cumulative. Counter resets are treated as a new counter.
            var rainDelta = current.RainfallMm - previous.RainfallMm;
            surfaceMm += rainDelta >= 0 ? rainDelta : Math.Max(0, current.RainfallMm);

            // Add completed physical lawn runs after drying the interval. This is deliberately
            // conservative: freshly applied water is not assumed to have dried before its end time.
            while (runIndex < orderedRuns.Count && orderedRuns[runIndex].EndedAt <= end)
            {
                if (orderedRuns[runIndex].EndedAt > start)
                    surfaceMm += Math.Max(0, orderedRuns[runIndex].AppliedMm);
                runIndex++;
            }
        }

        var lastObservationTime = observations[^1].Timestamp ?? cutoff;
        while (runIndex < orderedRuns.Count)
        {
            if (orderedRuns[runIndex].EndedAt > lastObservationTime && orderedRuns[runIndex].EndedAt <= now)
                surfaceMm += Math.Max(0, orderedRuns[runIndex].AppliedMm);
            runIndex++;
        }

        return Math.Max(0, surfaceMm);
    }

    internal static double EstimateDryingMm(
        WeatherObservation first,
        WeatherObservation second,
        double hours,
        IrrigationConfiguration options)
    {
        if (hours <= 0) return 0;

        var temperature = (first.TemperatureC + second.TemperatureC) / 2.0;
        var humidity = Math.Clamp((first.HumidityPct + second.HumidityPct) / 2.0, 0, 100);
        var wind = Math.Max(0, (first.WindSpeedKmh + second.WindSpeedKmh) / 2.0);
        var lux = Math.Max(0, (first.LightLux + second.LightLux) / 2.0);
        var solarWm2 = options.LuxPerWattM2 > 0 ? lux / options.LuxPerWattM2 : 0;

        // Deliberately conservative surface-drying heuristic. It is not the root-zone ET0 model:
        // solar, warmth, wind and dry air accelerate the disappearance of near-surface water.
        var rateMmPerHour = 0.01
                            + solarWm2 * 0.00018
                            + Math.Max(0, temperature - 10) * 0.006
                            + wind * 0.008
                            + Math.Max(0, 70 - humidity) * 0.004;

        rateMmPerHour = Math.Clamp(rateMmPerHour * Math.Max(0, options.MowingDryingFactor), 0, 0.6);
        return rateMmPerHour * hours;
    }

    internal static double RainForPeriod(
        IReadOnlyList<WeatherObservation> orderedObservations,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        if (orderedObservations.Count < 2 || end <= start) return 0;
        var window = new List<WeatherObservation>();
        var baseline = orderedObservations.LastOrDefault(x => x.Timestamp < start);
        if (baseline is not null) window.Add(baseline);
        window.AddRange(orderedObservations.Where(x => x.Timestamp >= start && x.Timestamp <= end));
        if (window.Count < 2) return 0;

        double total = 0;
        for (var i = 1; i < window.Count; i++)
        {
            var delta = window[i].RainfallMm - window[i - 1].RainfallMm;
            total += delta >= 0 ? delta : Math.Max(0, window[i].RainfallMm);
        }
        return total;
    }

    private IrrigationState LoadState()
    {
        try
        {
            if (!File.Exists(_options.StateFile)) return new IrrigationState();
            return JsonSerializer.Deserialize<IrrigationState>(File.ReadAllText(_options.StateFile)) ?? new IrrigationState();
        }
        catch
        {
            return new IrrigationState();
        }
    }
}
