# Irrigation

The gateway can combine local WN90LP observations with an Open-Meteo forecast and return irrigation runtimes for Loxone.

## Loxone -> gateway weather feed

Send the current weather values once per minute with one GET request:

`GET /Irrigation/weather?temperatureC=18.2&humidityPct=63&pressureHpa=955.6&windSpeedKmh=8.6&lightLux=15320&rainfallMm=4.7`

Query parameters:

- `temperatureC` — air temperature in °C
- `humidityPct` — relative humidity in %
- `pressureHpa` — absolute pressure in hPa
- `windSpeedKmh` — wind speed in km/h
- `lightLux` — light level in lux
- `rainfallMm` — WN90LP cumulative rainfall counter in mm
- `timestamp` — optional ISO-8601 timestamp; normally omit this so gateway receive time is used

`rainfallMm` is the WN90LP cumulative rainfall register (decimal register 364 after scaling to mm). Counter resets are handled by the gateway.

If a timestamp is supplied, it is accepted only when it is within `MaximumTimestampSkewMinutes` of gateway time. Otherwise gateway receive time is used. Observation retention is always based on gateway time, so a bad caller timestamp cannot purge valid history.

The gateway retains four days of observations in `Api:IrrigationConfiguration:StateFile` so rolling ET0 and rainfall calculations survive service restarts.

## Forecast

The gateway requests the next `ForecastHours` from Open-Meteo and uses hourly:

- precipitation
- FAO reference evapotranspiration (`et0_fao_evapotranspiration`)

Forecasts are refreshed every 30 minutes. If the forecast service is temporarily unavailable, the last cached forecast is used only until the horizon represented by that forecast has expired. After that, forecast rain and ET0 fall back to zero rather than allowing stale rain to suppress irrigation indefinitely.

Forecast rain reduces the current irrigation requirement. Forecast ET0 is returned for diagnostics but is intentionally not pre-watered.

## Calculation

Observed ET0 uses the FAO-56 Penman-Monteith daily equation over the rolling previous 24 hours. Local WN90LP inputs provide temperature, humidity, pressure, wind, light and rain. Light is converted to approximate solar irradiance with the configurable `LuxPerWattM2` coefficient and integrated over the observations.

Automatic irrigation is disabled until the gateway has a complete rolling 24-hour observation window. The first observation must be within `MaximumObservationEdgeGapMinutes` of the start of the window and the latest observation within the same tolerance of the current time. `GET /Irrigation/data-complete` exposes this readiness as `0` or `1`.

Rainfall windows retain the final cumulative-counter observation before each 24-hour/72-hour cutoff as their baseline, so rain immediately around the window boundary is not lost.

The current requirement is:

`max(0, observed ET0 - effective measured rain - effective forecast rain)`

Irrigation is enabled when the local data window is complete and this exceeds `MinimumIrrigationMm`.

Each zone then applies its exposure coefficient and application rate:

`runtime seconds = deficit mm * exposure / applicationRateMmPerHour * 3600`

**ApplicationRateMmPerHour must be measured/calibrated for every zone.** The default `10` values are placeholders, especially for drip zones.

## Zone mapping

| Zone | Type | Exposure |
| --- | --- | ---: |
| V1 | Lawn, medium-high sun | 0.85 |
| V2 | Plants/drip, medium sun | 0.75 |
| V3 | Plants/drip, less sun | 0.60 |
| V4 | Lawn, least sun | 0.65 |
| V5 | Plants/drip, medium sun | 0.75 |
| V6 | Lawn, most sun | 1.00 |

## Loxone-friendly outputs

The full diagnostic response is available at:

`GET /Irrigation`

Scalar endpoints suitable for Virtual HTTP Inputs:

- `GET /Irrigation/irrigate` -> `0` or `1`
- `GET /Irrigation/data-complete` -> `0` until a complete 24-hour local weather window is available, otherwise `1`
- `GET /Irrigation/et0` -> observed ET0, mm/24h
- `GET /Irrigation/rain24h` -> measured rain, mm
- `GET /Irrigation/rain72h` -> measured rain, mm
- `GET /Irrigation/forecast-rain` -> forecast rain, mm
- `GET /Irrigation/deficit` -> irrigation deficit, mm
- `GET /Irrigation/zone/V1` through `/V6` -> runtime in seconds

Map the six zone runtime endpoints to the Loxone Irrigation block `Tv1` through `Tv6`, and use `/Irrigation/irrigate` to gate/trigger the automatic irrigation cycle.

The weather-ingestion route is intentionally not authenticated because this gateway is designed to run only on the trusted LAN. Do not expose the irrigation endpoints directly to the public internet.
