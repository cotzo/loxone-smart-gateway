# Irrigation

The gateway combines local WN90LP observations with an Open-Meteo forecast and maintains a persistent soil-water deficit for each physical irrigation area.

## Weather ingestion

Loxone stages the latest values using:

- `GET /Irrigation/weather/temperature/<v>`
- `GET /Irrigation/weather/humidity/<v>`
- `GET /Irrigation/weather/pressure/<v>`
- `GET /Irrigation/weather/wind/<v>`
- `GET /Irrigation/weather/light/<v>`
- `GET /Irrigation/weather/rainfall/<v>`

Then commit one observation with:

`GET /Irrigation/weather/commit`

Staged values survive unchanged between commits and are hydrated from the latest persisted observation after a service restart. The rainfall value is the WN90LP cumulative counter; counter resets are handled by the gateway.

Weather observations are retained for 8 days. Irrigation-run history is retained for 30 days. Both are stored in `StateFile` and therefore require a persistent volume.

## Soil-water balance

Completed local calendar days are processed once. A day is accepted only when weather observations cover both edges of the day within `CompletedDayEdgeGapMinutes`; an incomplete day is skipped instead of permanently adding a misleading ET0 value.

For each balance group:

`new deficit = clamp(previous deficit + ET0 * exposure - effective rain, 0, MaximumDeficitMm)`

Forecast rain does not modify the stored balance. It only reduces the current recommendation:

`required now = max(0, stored deficit - forecast rain * EffectiveRainFactor * ForecastRainWeight)`

A group is eligible to irrigate when `required now >= IrrigationTriggerMm`.

`ForecastRainWeight` defaults to `0.75`, so property-scale forecast uncertainty does not suppress irrigation as aggressively as a weight of 1.0.

## Balance groups

Multiple valves that irrigate the same physical root zone must share a `BalanceGroup`.

For this installation V1 and V4 irrigate the same lawn from opposite sides, so both use `BalanceGroup=LawnEast`. V6 is a separate lawn group. Other zones can omit `BalanceGroup`; the zone ID is then used automatically.

All zones in one balance group should have the same exposure value. The gateway logs a warning if they differ.

The runtime for a shared group is calculated from the sum of the member valves' **physical** application rates. All valves in the group receive the same recommended runtime:

`runtime = required mm / sum(group application rates mm/h) * 3600`

For V1/V4, the measured combined effective rate was about 23.3 mm/h, therefore each physical circuit should be configured at about `11.65 mm/h`, not 23.3 mm/h. V6 remains about `25.4 mm/h`.

## Irrigation feedback and history

After a valve actually runs, Loxone reports it using a unique event ID:

`GET /Irrigation/zone/{id}/applied/{runtimeSeconds}/{eventId}?type=Irrigation`

The event ID makes reporting idempotent. Retrying the same request does not subtract water twice and does not create duplicate history.

Supported run types are:

- `Irrigation` — stored in history and subtracts delivered water from the balance group.
- `Rinse` — stored in history but does not modify the irrigation balance.
- `Manual` — stored in history but does not modify the irrigation balance.

Each history record contains event ID, zone, balance group, type, runtime, applied mm, derived start time and end time. Retrieve the newest-first 30-day history with:

`GET /Irrigation/history`

## Safety limits

`MaximumDeficitMm` bounds stored soil depletion. `MaximumZoneRuntimeSeconds` caps any single recommended valve runtime. The defaults are 15 mm and 1800 seconds respectively.

The gateway intentionally does not implement cycle-and-soak scheduling; Loxone remains responsible for how a recommended runtime is physically scheduled.

## Loxone-friendly outputs

- `GET /Irrigation` — full diagnostics
- `GET /Irrigation/irrigate` — `0` or `1`
- `GET /Irrigation/data-complete` — diagnostic full rolling-24h local coverage flag
- `GET /Irrigation/et0` — observed rolling ET0
- `GET /Irrigation/rain24h` — measured rain
- `GET /Irrigation/rain72h` — measured rain
- `GET /Irrigation/forecast-rain` — forecast rain
- `GET /Irrigation/deficit` — maximum stored balance deficit
- `GET /Irrigation/zone/V1` through `/V6` — recommended runtime seconds

## Environment configuration

Irrigation configuration is intentionally not stored in `appsettings.json`; configure it with environment variables.

Important general values:

```text
Api__IrrigationConfiguration__Latitude=46.77
Api__IrrigationConfiguration__Longitude=23.59
Api__IrrigationConfiguration__ElevationM=477
Api__IrrigationConfiguration__LuxPerWattM2=120
Api__IrrigationConfiguration__EffectiveRainFactor=0.8
Api__IrrigationConfiguration__ForecastRainWeight=0.75
Api__IrrigationConfiguration__IrrigationTriggerMm=5.0
Api__IrrigationConfiguration__MaximumDeficitMm=15.0
Api__IrrigationConfiguration__MaximumZoneRuntimeSeconds=1800
Api__IrrigationConfiguration__ForecastHours=24
Api__IrrigationConfiguration__MaximumTimestampSkewMinutes=5
Api__IrrigationConfiguration__MaximumObservationEdgeGapMinutes=15
Api__IrrigationConfiguration__CompletedDayEdgeGapMinutes=30
Api__IrrigationConfiguration__TimeZoneId=Europe/Bucharest
Api__IrrigationConfiguration__StateFile=/data/irrigation-state.json
```

The weather-ingestion and irrigation routes are intentionally unauthenticated because the gateway is designed for the trusted LAN only. Do not expose them directly to the public internet.
