# Irrigation

The gateway can combine local WN90LP observations with an Open-Meteo forecast and return irrigation runtimes for Loxone.

## Loxone -> gateway weather feed

Send the current weather values once per minute:

`POST /Irrigation/weather`

```json
{
  "temperatureC": 18.2,
  "humidityPct": 63,
  "pressureHpa": 955.6,
  "windSpeedKmh": 8.6,
  "lightLux": 15320,
  "rainfallMm": 4.7
}
```

`rainfallMm` is the WN90LP cumulative rainfall register (decimal register 364 after scaling to mm). Counter resets are handled by the gateway.

The gateway retains four days of observations in `Api:IrrigationConfiguration:StateFile` so rolling ET0 and rainfall calculations survive service restarts.

## Forecast

The gateway requests the next `ForecastHours` from Open-Meteo and uses hourly:

- precipitation
- FAO reference evapotranspiration (`et0_fao_evapotranspiration`)

Forecasts are cached for 30 minutes. If the forecast service is unavailable, calculation continues from local observations and the last cached forecast if one exists.

Forecast rain reduces the current irrigation requirement. Forecast ET0 is returned for diagnostics but is intentionally not pre-watered.

## Calculation

Observed ET0 uses the FAO-56 Penman-Monteith daily equation over the rolling previous 24 hours. Local WN90LP inputs provide temperature, humidity, pressure, wind, light and rain. Light is converted to approximate solar irradiance with the configurable `LuxPerWattM2` coefficient and integrated over the observations.

The current requirement is:

`max(0, observed ET0 - effective measured rain - effective forecast rain)`

Irrigation is enabled when this exceeds `MinimumIrrigationMm`.

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
- `GET /Irrigation/et0` -> observed ET0, mm/24h
- `GET /Irrigation/rain24h` -> measured rain, mm
- `GET /Irrigation/rain72h` -> measured rain, mm
- `GET /Irrigation/forecast-rain` -> forecast rain, mm
- `GET /Irrigation/deficit` -> irrigation deficit, mm
- `GET /Irrigation/zone/V1` through `/V6` -> runtime in seconds

Map the six zone runtime endpoints to the Loxone Irrigation block `Tv1` through `Tv6`, and use `/Irrigation/irrigate` to gate/trigger the automatic irrigation cycle.
