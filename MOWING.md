# Mowing safety

The gateway exposes a separate surface-wetness model for robotic mower protection. This is intentionally different from the root-zone irrigation deficit: irrigation asks whether plants need water; mowing asks whether the lawn surface is dry and firm enough for traction.

## Outputs

- `GET /Irrigation/mowing-allowed` — `1` when mowing is allowed, otherwise `0`.
- `GET /Irrigation/lawn-wetness` — estimated near-surface water in mm.
- `GET /Irrigation/mowing` — full diagnostics.

`mowing-allowed` is true only when all of the following are true:

1. the latest local weather observation is fresh;
2. estimated lawn surface wetness is at or below `MowingAllowedWetnessMm`;
3. no measurable rain occurred during the recent-rain window;
4. no configured lawn irrigation valve is currently active;
5. the heavy-rain lockout is not active.

Forecast rain is deliberately not used to block mowing. If the lawn is currently dry, a future forecast does not make it unsafe to mow.

## Surface wetness model

Measured rain and all completed physical runs on zones configured with `Type=Lawn` add water to the surface reservoir. This includes `Irrigation`, `Rinse`, and `Manual` runs because all of them physically wet the grass.

The surface reservoir dries using the local weather observations. Solar radiation, higher temperature, wind, and low relative humidity increase the drying rate. Long gaps in observations are not treated as known drying periods, making the estimate conservative.

The default model replays 48 hours of history and caps modeled drying at 0.6 mm/h. `MowingDryingFactor` can be tuned after comparing the estimate with the real lawn.

## Heavy-rain protection

The default heavy-rain rule blocks mowing when at least 10 mm of measured rain occurred during the previous 12 hours. This is a hard safety rule for soft ground and slopes even if the surface reservoir estimate has already fallen below the normal mowing threshold.

## Environment variables

```text
Api__IrrigationConfiguration__MowingAllowedWetnessMm=1.0
Api__IrrigationConfiguration__MowingWetnessLookbackHours=48
Api__IrrigationConfiguration__MowingRainingNowMinutes=15
Api__IrrigationConfiguration__MowingHeavyRainThresholdMm=10.0
Api__IrrigationConfiguration__MowingHeavyRainLockoutHours=12
Api__IrrigationConfiguration__MowingMaximumDryingGapMinutes=30
Api__IrrigationConfiguration__MowingMaximumWeatherAgeMinutes=10
Api__IrrigationConfiguration__MowingDryingFactor=1.0
```

For the current Loxone/Husqvarna integration, poll `/Irrigation/mowing-allowed` as a digital permission signal. A value of `0` should park or inhibit the mower; a value of `1` should only permit the normal mowing schedule rather than forcing the mower to start immediately.
