# Loxone Smart Gateway

A local, self-hosted web application that discovers Loxone state UUIDs, Hue lights/groups and Tuya datapoints, then continuously applies mapped Loxone state to Hue or Tuya. It also provides the existing irrigation planner and weather-ingestion endpoints.

## Deploy

Set a long random admin password in `.env`:

```dotenv
GATEWAY_ADMIN_PASSWORD=replace-with-a-long-random-secret
```

Then run:

```bash
docker compose up -d
```

Open `http://<gateway-ip>:8080`. The named `gateway-data` volume stores encrypted settings, Data Protection keys and irrigation history. Back up and retain the entire volume; losing the keys makes saved provider credentials unreadable. For access beyond a trusted LAN, put the service behind a TLS reverse proxy. The UI login token is held in browser session storage.

## Configure

1. Add each Loxone Miniserver and its account, then save and discover states. Use a local account with access to the visualization structure. Loxone exposes state subscriptions for controls present in its visualization.
2. Add Hue bridges with their host and application key, then discover lights and grouped lights.
3. Add Tuya LAN devices with name, device ID, IP, local key and protocol version 3.4 or 3.5. Read datapoints after saving. TinyTuya's LAN scan can find device IDs and versions but cannot retrieve local keys; its setup wizard uses a linked Tuya IoT cloud project.
4. Add mappings: select a Miniserver state UUID, Hue or Tuya connection, resource/datapoint and value mode. Hue supports light/grouped-light resources and DIM, RGB, TUNABLE and ONOFF command modes.
5. Configure irrigation in the UI. In Irrigation â†’ Loxone Setup, select the sensor states and irrigation function block; import its zones, then review each zone's type, balance group, exposure multiplier and measured application rate. Configure the irrigation-required and Lawn Dry controls there as well.

The gateway subscribes to Loxone state updates and applies the initial state snapshot as well as subsequent changes. After a Miniserver reconnect, a fresh snapshot restores mapped provider state. Failed provider commands remain queued in memory and retry with capped exponential backoff; once accepted by Hue/Tuya, the command is considered synchronized. The gateway does not independently read back target state. This is command synchronization, not a durable event journal: if the gateway itself restarts while a target is offline, the next Loxone snapshot replays the current state.

Mapping modes:

| Mode | Behavior |
| --- | --- |
| `direct` | Sends the numeric Loxone value (Tuya JSON number; Hue interpretation depends on selected light command type). |
| `onOff` | Converts zero to off/false and nonzero to on/true. |
| `loxoneDmx` | Uses the existing Loxone packed Lumitech color conversion; intended for Hue RGB/tunable mappings. |

## Loxone transport limitation

The current Loxone communicator library used by the gateway supports local `ws://` connections, not encrypted `wss://`. `Use TLS` is therefore not enabled in the UI. Keep Loxone access on the trusted local network and use the Miniserver's local WebSocket port. The service uses Loxone's authenticated state subscription rather than requiring per-light Virtual Output HTTP commands.

## Irrigation integration

Irrigation weather values, zone activity, recommended durations, and the `Irrigation Required` / `Lawn Dry` controls are now synchronized directly through the saved Loxone state selections. Configure them in Irrigation â†’ Loxone Setup; no legacy `/Irrigation/...` HTTP virtual inputs, weather commit calls, or run-reporting callbacks are required. Status, stored observations, balances, recommendations, and decision explanations are shown in the Irrigation â†’ Status UI. Irrigation REST operations are authenticated under `/api/irrigation`; the UI uses `/api/irrigation/loxone/blocks`, `/preview`, and `/import` to discover and import zones.

The old unauthenticated `/Irrigation/...`, `/PhilipsHue/...`, and `/Tuya/...` routes are not part of the current API. `/health` is available for container health checks. Prometheus metrics are available at `/metrics` when `Configuration__EnablePrometheus=true`.

### Prometheus metrics

Provider metrics use only bounded labels (`provider`, Hue `bridge` display name, Hue `resource_type`, and command `outcome`); they deliberately omit mapping, light, Tuya device, zone, and datapoint IDs.

- `gateway_provider_queue_wait` â€” histogram, enqueue to send start, including queue pacing.
- `gateway_provider_event_to_dispatch` â€” histogram, Loxone state receipt to provider send start.
- `gateway_provider_request_duration` â€” histogram, Hue HTTP request or full Tuya local connect-and-send duration.
- `gateway_provider_commands` â€” counter by provider/resource type and success, failure, or invalid input outcome.
- `gateway_provider_commands_coalesced`, `gateway_provider_command_retries`, `gateway_provider_command_failures` â€” replaced pending values, scheduled retries, and commands dropped after exhausting retries.
- `gateway_hue_group_optimizations`, `gateway_hue_group_requests`, `gateway_hue_group_requests_saved` â€” exact-coverage Hue group optimizations applied, original grouped-light requests represented, and requests avoided. The dashboard calculates the reduction percentage from saved / represented requests.
- `gateway_provider_queue_pending` â€” current unique pending commands.
- `irrigation_weather_observations_committed`, `irrigation_weather_observation_interval` â€” committed observation count and spacing.
- `irrigation_weather_observations_stored`, `irrigation_weather_latest_age`, `irrigation_weather_history_span` â€” retained weather history size, freshness, and time coverage.
- `irrigation_weather_observed_temperature`, `irrigation_weather_observed_humidity`, `irrigation_weather_observed_pressure`, `irrigation_weather_observed_wind_speed`, `irrigation_weather_observed_light`, `irrigation_weather_observed_rainfall_counter` â€” latest persisted mapped sensor points.
- `irrigation_runs_completed`, `irrigation_run_duration`, `irrigation_run_water_applied`, `irrigation_runs_stored` â€” completed irrigation activity and retained run history.
- `irrigation_balance_groups` â€” configured water-balance group count.
- `irrigation_decision_required`, `irrigation_decision_weather_complete`, `irrigation_decision_forecast_available`, `irrigation_decision_latest_age`, `irrigation_decision_calculation_duration` â€” latest decision state and calculation latency.
- `irrigation_decision_rain_observed_24h`, `irrigation_decision_rain_observed_72h`, `irrigation_decision_et0_observed_24h`, `irrigation_decision_rain_forecast`, `irrigation_decision_et0_forecast`, `irrigation_decision_water_deficit` â€” latest calculated weather and water-balance values.
- `irrigation_lawn_dry_allowed`, `irrigation_lawn_dry_surface_wetness`, `irrigation_lawn_dry_weather_fresh`, `irrigation_lawn_dry_raining`, `irrigation_lawn_dry_irrigation_running`, `irrigation_lawn_dry_heavy_rain_lockout` â€” latest Lawn Dry output and main blocking conditions.

Prometheus applies its normal `_bucket`, `_sum`, and `_count` suffixes to histograms. An age gauge is `0` until its first observation/calculation.

An import-ready Grafana dashboard covering per-Hue-bridge latency/queues, irrigation decisions, Lawn Dry status, latest temperature/humidity/pressure/wind/light/rain readings and their histories, weather history, and completed runs is available at [docs/grafana/loxone-smart-gateway-overview.json](docs/grafana/loxone-smart-gateway-overview.json). Import it in Grafana and select the Prometheus data source that scrapes this gateway. The dashboard uses regex selectors for unit-bearing histogram/gauge names to tolerate Prometheus exporter unit suffixes.

## Development

For tracked implementation findings and future work, see the [GitHub issues](https://github.com/cotzo/loxone-smart-gateway/issues).

Requires the .NET 10 SDK. Set `Gateway__AdminPassword` to at least 16 characters and `Gateway__DataDirectory` to a writable directory, then run:

```bash
dotnet run --project service
```
