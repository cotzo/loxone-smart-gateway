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

## Required Loxone irrigation flow

Reading a zone runtime does **not** consume or reset its soil-water balance. Loxone must report each completed main-irrigation valve run back to the gateway. Without this callback the gateway has no way to know that water was actually applied and will continue recommending irrigation for the outstanding deficit.

The intended automatic flow is:

1. Loxone polls `GET /Irrigation/irrigate` and the zone runtime endpoints such as `GET /Irrigation/zone/V1`.
2. When the scheduled main irrigation window starts, Loxone runs each required valve for the runtime it actually uses. A zero runtime means that valve is skipped.
3. After each valve finishes, Loxone immediately reports the **actual elapsed runtime**, not merely the originally requested runtime, using:

   `GET /Irrigation/zone/{id}/applied/{runtimeSeconds}/{eventId}?type=Irrigation`

4. `eventId` must uniquely identify that physical valve run and must remain identical if Loxone retries the HTTP request. A practical format is `yyyyMMdd-HHmm-{zone}`, for example:

   `GET /Irrigation/zone/V6/applied/708/20260821-0500-V6?type=Irrigation`

5. Only a successful main irrigation run should be reported as `type=Irrigation`. If a valve did not actually open/run, do not report it as applied water.

For shared balance groups such as V1/V4, report **both physical valve runs independently** after they complete. Each callback subtracts the water physically delivered by that valve from the shared `LawnEast` balance. For example, if both valves actually run 600 seconds:

`GET /Irrigation/zone/V1/applied/600/20260821-0500-V1?type=Irrigation`

`GET /Irrigation/zone/V4/applied/600/20260821-0510-V4?type=Irrigation`

The event ID makes reporting idempotent. Retrying either exact event does not subtract water twice and does not create duplicate history.

Short dog-rinse cycles may optionally be recorded without changing the soil-water balance:

`GET /Irrigation/zone/V6/applied/90/20260821-2230-V6?type=Rinse`

Manual runs can likewise use `type=Manual`. Only `type=Irrigation` modifies the persistent soil-water balance.

## Irrigation feedback and history

Supported run types are:

- `Irrigation` — stored in history and subtracts delivered water from the balance group.
- `Rinse` — stored in history but does not modify the irrigation balance.
- `Manual` — stored in history but does not modify the irrigation balance.

Each history record contains event ID, zone, balance group, type, runtime, applied mm, derived start time and end time. Retrieve the newest-first 30-day history with:

`GET /Irrigation/history`

## Safety limits

`MaximumDeficitMm` bounds stored soil depletion. `MaximumZoneRuntimeSeconds` caps any single recommended valve runtime. The defaults are 15 mm and 1800 seconds respectively.

If a recommended runtime is capped, only the water represented by the runtime that actually executes is removed when Loxone sends the callback. Any remaining deficit stays in the persistent balance and can be irrigated during a later run.

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
- `GET /Irrigation/history` — newest-first 30-day irrigation/run history

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

For V1/V4 specifically:

```text
Api__IrrigationConfiguration__Zones__0__BalanceGroup=LawnEast
Api__IrrigationConfiguration__Zones__0__ApplicationRateMmPerHour=11.65
Api__IrrigationConfiguration__Zones__3__BalanceGroup=LawnEast
Api__IrrigationConfiguration__Zones__3__ApplicationRateMmPerHour=11.65
```

The weather-ingestion and irrigation routes are intentionally unauthenticated because the gateway is designed for the trusted LAN only. Do not expose them directly to the public internet.
