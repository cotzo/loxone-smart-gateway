namespace loxone.smart.gateway.Api.Irrigation;

public sealed class LawnDryService
{
    private readonly IrrigationService _irrigation;
    private readonly IrrigationRunTracker _runTracker;
    private readonly IrrigationMetrics _metrics;

    public LawnDryService(IrrigationService irrigation, IrrigationRunTracker runTracker, IrrigationMetrics metrics)
    {
        _irrigation = irrigation;
        _runTracker = runTracker;
        _metrics = metrics;
    }

    public async Task<LawnDryStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var options = _irrigation.GetConfigurationSnapshot();
        // Use complete retained history; truncating the dashboard's recent-sample view would
        // undercount rain/irrigation and distort the mowing wetness estimate.
        var state = await _irrigation.GetStoredDataAsync(cancellationToken, observationLimit: 0, runLimit: 0);
        var now = DateTimeOffset.UtcNow;
        var lawnZoneIds = options.Zones
            .Where(x => string.Equals(x.Type, "Lawn", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Check the tracker first. If a stop is currently being finalized this waits behind the
        // tracker's gate. Only after that transition is complete do we read persisted run history,
        // so the snapshot cannot miss both the active run and its completed history record.
        var irrigationRunning = await _runTracker.IsAnyZoneActiveAsync(lawnZoneIds, cancellationToken);
        var observations = state.Observations
            .Where(x => x.Timestamp is not null)
            .OrderBy(x => x.Timestamp)
            .ToList();

        var lookback = TimeSpan.FromHours(Math.Max(6, options.MowingWetnessLookbackHours));
        var cutoff = now - lookback;
        var baseline = observations.LastOrDefault(x => x.Timestamp < cutoff);
        var window = new List<WeatherObservation>();
        if (baseline is not null) window.Add(baseline);
        window.AddRange(observations.Where(x => x.Timestamp >= cutoff));

        var lawnRuns = state.IrrigationRuns
            .Where(x => lawnZoneIds.Contains(x.ZoneId) && x.EndedAt >= cutoff && x.EndedAt <= now)
            .OrderBy(x => x.EndedAt)
            .ToList();

        var wetnessMm = CalculateSurfaceWetnessMm(window, lawnRuns, cutoff, now, options);
        var rainingNowWindow = now.AddMinutes(-Math.Max(1, options.MowingRainingNowMinutes));
        var rainingNow = RainForPeriod(observations, rainingNowWindow, now) > 0.01;

        var heavyRainWindow = now.AddHours(-Math.Max(1, options.MowingHeavyRainLockoutHours));
        var rainDuringLockout = RainForPeriod(observations, heavyRainWindow, now);
        var heavyRainLockout = rainDuringLockout >= Math.Max(0, options.MowingHeavyRainThresholdMm);

        var latest = observations.LastOrDefault()?.Timestamp;
        var weatherFresh = latest is not null &&
                           latest >= now.AddMinutes(-Math.Max(1, options.MowingMaximumWeatherAgeMinutes));
        var historyReady = HasSufficientWeatherHistory(observations, now, options);
        var weatherDataFresh = weatherFresh && historyReady;

        var threshold = Math.Max(0, options.LawnDryWetnessThresholdMm);
        var wetnessAllowed = wetnessMm <= threshold;
        var allowed = weatherDataFresh && wetnessAllowed && !rainingNow && !irrigationRunning && !heavyRainLockout;
        var latestText = latest?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "no saved sample";
        var checks = new[]
        {
            new IrrigationDecisionCheck("Weather freshness", weatherFresh,
                weatherFresh ? $"Latest committed sample: {latestText}; limit {Math.Max(1, options.MowingMaximumWeatherAgeMinutes)} min." : $"Latest committed sample is missing or older than {Math.Max(1, options.MowingMaximumWeatherAgeMinutes)} min ({latestText})."),
            new IrrigationDecisionCheck("Weather history", historyReady,
                historyReady ? "At least two ordered weather samples are available." : "At least two ordered, sufficiently recent weather samples are required."),
            new IrrigationDecisionCheck("Surface wetness", wetnessAllowed,
                $"Estimated {Math.Round(wetnessMm, 2):F2} mm; allowed maximum {threshold:F2} mm."),
            new IrrigationDecisionCheck("Rain now", !rainingNow,
                rainingNow ? $"Rain was detected in the last {Math.Max(1, options.MowingRainingNowMinutes)} min." : $"No rain detected in the last {Math.Max(1, options.MowingRainingNowMinutes)} min."),
            new IrrigationDecisionCheck("Lawn irrigation", !irrigationRunning,
                irrigationRunning ? "A configured lawn zone is currently active." : "No configured lawn zone is currently active."),
            new IrrigationDecisionCheck("Heavy-rain lockout", !heavyRainLockout,
                heavyRainLockout ? $"{Math.Round(rainDuringLockout, 2):F2} mm reached the {Math.Max(0, options.MowingHeavyRainThresholdMm):F2} mm threshold." : $"{Math.Round(rainDuringLockout, 2):F2} mm is below the {Math.Max(0, options.MowingHeavyRainThresholdMm):F2} mm threshold.")
        };

        var status = new LawnDryStatus(
            allowed,
            Math.Round(wetnessMm, 2),
            Math.Round(threshold, 2),
            weatherDataFresh,
            rainingNow,
            irrigationRunning,
            heavyRainLockout,
            Math.Round(rainDuringLockout, 2),
            now,
            checks);
        _metrics.RecordLawnDryDecision(status);
        return status;
    }

    internal static bool HasSufficientWeatherHistory(IReadOnlyList<WeatherObservation> observations, DateTimeOffset now, IrrigationConfiguration options)
    {
        if (observations.Count < 2) return false;
        var latest = observations[^1].Timestamp;
        if (latest is null || latest < now.AddMinutes(-Math.Max(1, options.MowingMaximumWeatherAgeMinutes))) return false;
        var previous = observations.Take(observations.Count - 1).LastOrDefault(x => x.Timestamp is not null);
        return previous?.Timestamp is not null && latest.Value > previous.Timestamp.Value;
    }

    internal static double CalculateSurfaceWetnessMm(IReadOnlyList<WeatherObservation> observations, IReadOnlyList<IrrigationRun> lawnRuns, DateTimeOffset cutoff, DateTimeOffset now, IrrigationConfiguration options)
    {
        var orderedRuns = lawnRuns.Where(x => x.EndedAt >= cutoff && x.EndedAt <= now).OrderBy(x => x.EndedAt).ToList();
        if (observations.Count == 0) return orderedRuns.Sum(x => Math.Max(0, x.AppliedMm));

        var firstObservationTime = observations.Where(x => x.Timestamp is not null).Select(x => x.Timestamp!.Value).DefaultIfEmpty(now).Min();
        var surfaceMm = orderedRuns.Where(x => x.EndedAt < firstObservationTime).Sum(x => Math.Max(0, x.AppliedMm));
        var runIndex = orderedRuns.FindIndex(x => x.EndedAt >= firstObservationTime);
        if (runIndex < 0) runIndex = orderedRuns.Count;

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
            if (intervalHours <= maxGapHours) surfaceMm = Math.Max(0, surfaceMm - EstimateDryingMm(previous, current, intervalHours, options));
            var rainDelta = current.RainfallMm - previous.RainfallMm;
            surfaceMm += rainDelta >= 0 ? rainDelta : Math.Max(0, current.RainfallMm);
            while (runIndex < orderedRuns.Count && orderedRuns[runIndex].EndedAt <= end)
            {
                if (orderedRuns[runIndex].EndedAt > start) surfaceMm += Math.Max(0, orderedRuns[runIndex].AppliedMm);
                runIndex++;
            }
        }

        var lastObservationTime = observations[^1].Timestamp ?? cutoff;
        while (runIndex < orderedRuns.Count)
        {
            if (orderedRuns[runIndex].EndedAt > lastObservationTime && orderedRuns[runIndex].EndedAt <= now) surfaceMm += Math.Max(0, orderedRuns[runIndex].AppliedMm);
            runIndex++;
        }
        return Math.Max(0, surfaceMm);
    }

    internal static double EstimateDryingMm(WeatherObservation first, WeatherObservation second, double hours, IrrigationConfiguration options)
    {
        if (hours <= 0) return 0;
        var temperature = (first.TemperatureC + second.TemperatureC) / 2.0;
        var humidity = Math.Clamp((first.HumidityPct + second.HumidityPct) / 2.0, 0, 100);
        var wind = Math.Max(0, (first.WindSpeedKmh + second.WindSpeedKmh) / 2.0);
        var lux = Math.Max(0, (first.LightLux + second.LightLux) / 2.0);
        var solarWm2 = options.LuxPerWattM2 > 0 ? lux / options.LuxPerWattM2 : 0;
        var rateMmPerHour = 0.01 + solarWm2 * 0.00018 + Math.Max(0, temperature - 10) * 0.006 + wind * 0.008 + Math.Max(0, 70 - humidity) * 0.004;
        rateMmPerHour = Math.Clamp(rateMmPerHour * Math.Max(0, options.MowingDryingFactor), 0, 0.6);
        return rateMmPerHour * hours;
    }

    internal static double RainForPeriod(IReadOnlyList<WeatherObservation> orderedObservations, DateTimeOffset start, DateTimeOffset end)
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

}
