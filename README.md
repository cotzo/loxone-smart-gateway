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
5. Configure irrigation coordinates and model parameters. In Irrigation → Loxone Setup, select the Irrigation function block and import its live zone list. Then review each imported zone's type, balance group, exposure multiplier and measured application rate. The gateway generates its own stable zone ID (used by `/Irrigation/zone/{id}`) and retains the original Loxone zone ID separately.

The gateway subscribes to Loxone state updates and applies the initial state snapshot as well as subsequent changes. After a Miniserver reconnect, a fresh snapshot restores mapped provider state. Failed provider commands remain queued in memory and retry with capped exponential backoff; once accepted by Hue/Tuya, the command is considered synchronized. The gateway does not independently read back target state. This is command synchronization, not a durable event journal: if the gateway itself restarts while a target is offline, the next Loxone snapshot replays the current state.

Mapping modes:

| Mode | Behavior |
| --- | --- |
| `direct` | Sends the numeric Loxone value (Tuya JSON number; Hue interpretation depends on selected light command type). |
| `onOff` | Converts zero to off/false and nonzero to on/true. |
| `loxoneDmx` | Uses the existing Loxone packed Lumitech color conversion; intended for Hue RGB/tunable mappings. |

## Loxone transport limitation

The current Loxone communicator library used by the gateway supports local `ws://` connections, not encrypted `wss://`. `Use TLS` is therefore not enabled in the UI. Keep Loxone access on the trusted local network and use the Miniserver's local WebSocket port. The service uses Loxone's authenticated state subscription rather than requiring per-light Virtual Output HTTP commands.

## Irrigation endpoints

The `/Irrigation` routes remain for weather ingestion and irrigation results, including `/Irrigation/weather/{field}/{value}`, `/Irrigation/weather/commit`, `/Irrigation/zone/{id}`, and scalar outputs. Authenticated `/api/irrigation/loxone/blocks`, `/preview`, and `/import` support the UI's live Loxone block import. `/health` is available for container health checks. Prometheus metrics are available at `/metrics` when `Configuration__EnablePrometheus=true`.

## Configure irrigation in Loxone Config

This section describes the current gateway API contract. The gateway does not configure Loxone automatically: create the HTTP Virtual Outputs and Virtual Inputs in Loxone Config, then connect them to the weather sensors and irrigation logic. Replace `<gateway-host>` with a DNS name or IP address reachable from the Miniserver, and use the HTTP port exposed by your deployment (for example, `8092`). Keep this traffic on a trusted LAN or behind a secured reverse proxy; the irrigation routes currently use HTTP GET requests, including routes that record data.

### 1. Send weather measurements to the gateway

Create one HTTP Virtual Output in Loxone Config, for example `Gateway Irrigation Weather`, with these six commands. Use the actual sensor value as `<value>` and send each field in the units shown.

| Command | URL path | Value |
| --- | --- | --- |
| Temperature | `/Irrigation/weather/temperature/<value>` | °C |
| Relative humidity | `/Irrigation/weather/humidity/<value>` | percent, 0–100 |
| Pressure | `/Irrigation/weather/pressure/<value>` | hPa |
| Wind speed | `/Irrigation/weather/wind/<value>` | km/h |
| Light level | `/Irrigation/weather/light/<value>` | lux |
| Rainfall | `/Irrigation/weather/rainfall/<value>` | cumulative rainfall counter in mm |

For example, the temperature command URL is `http://<gateway-host>:<port>/Irrigation/weather/temperature/<value>`. Configure the command to substitute the corresponding Loxone value where `<value>` appears. Add a seventh command to the same Virtual Output:

```text
http://<gateway-host>:<port>/Irrigation/weather/commit
```

Trigger the six measurement commands first, then trigger `commit` once for that measurement cycle. The commit returns an error until all six fields have been received. Rainfall is treated as a cumulative counter; do not send an interval amount unless the sensor itself reports an interval amount and the gateway logic is changed to match. Use a consistent update cadence and avoid triggering commit for every individual sensor update.

### 2. Read irrigation decisions

Create HTTP Virtual Inputs for the values your Loxone logic needs. The gateway returns numeric values for scalar endpoints; runtimes are seconds and rainfall/ET0/deficit/wetness values are millimeters.

| Loxone input | GET URL | Meaning |
| --- | --- | --- |
| Irrigate | `/Irrigation/irrigate` | `1` when any configured zone has a non-zero runtime; otherwise `0` |
| Zone runtime | `/Irrigation/zone/<zone-id>` | Recommended runtime in seconds for that configured zone |
| Weather data complete | `/Irrigation/data-complete` | `1` when recent observation coverage passes the configured completeness check |
| Water deficit | `/Irrigation/deficit` | Highest stored balance across balance groups, in mm |
| Forecast rain | `/Irrigation/forecast-rain` | Forecast rainfall over the configured forecast horizon, in mm |
| Observed ET0 | `/Irrigation/et0` | Observed reference evapotranspiration over the last 24 hours, in mm |
| Rain, 24 hours | `/Irrigation/rain24h` | Rainfall over the last 24 hours, in mm |
| Rain, 72 hours | `/Irrigation/rain72h` | Rainfall over the last 72 hours, in mm |
| Mowing allowed | `/Irrigation/mowing-allowed` | `1` only when the mowing safety checks pass; otherwise `0` |
| Lawn wetness | `/Irrigation/lawn-wetness` | Estimated lawn surface wetness, in mm |

Use `/Irrigation` for the full irrigation result and `/Irrigation/mowing` for the full mowing status when a JSON-capable consumer or diagnostic view is useful. Configure Loxone to poll decision inputs at a moderate interval (for example, every 1–5 minutes), not on a sub-second cycle. Zone runtimes are recommendations: have the Loxone logic enforce its own master enable, schedule, and safety interlocks before opening valves. The gateway does not actuate irrigation valves itself.

### 3. Import zones and enable automatic run tracking

After saving and refreshing the Loxone resource catalog, open Irrigation → Loxone Setup. Select the correct Irrigation function block, preview its current zone list, then import. The gateway stores the Loxone block's `active`, `zones` and `currentZone` state UUIDs, creates or updates gateway zone records, and watches `active` plus `currentZone` on its existing Loxone subscription. It records runs only while the block is active; no per-zone run-reporting Virtual Outputs are needed. With `active=true`, `currentZone=-1` means no selected zone, `0`–`7` select the corresponding zone in the imported `zones` array, and `8` means all zones are active.

Imported Loxone IDs and names are kept separately from gateway zone IDs, which are the keys for existing water-balance history and `/Irrigation/zone/<gateway-zone-id>`. Re-import updates names and source IDs while preserving gateway IDs, balance groups and calibration values. It does not delete zones absent from a later export, to avoid silently invalidating stored balances. New zones default to type `Other`, exposure `1`, and application rate `10 mm/hour`; review and adjust these before enabling automatic watering or mower safety logic. Configured zone duration from Loxone is displayed in preview but does not replace the measured application-rate calibration.

### 4. Commission and verify

1. Save the Loxone connection and refresh its resources. Select the intended Irrigation block, preview it, and import. Confirm every imported zone's type, balance group, and measured application rate in mm/hour.
2. Trigger one complete weather cycle. Check that the commit succeeds and that `/Irrigation` shows the expected observation-derived values. A failed commit means one or more of the six weather field commands did not reach the gateway.
3. Read each `/Irrigation/zone/<zone-id>` input and verify the returned value is plausible before allowing automatic watering.
4. Start and stop one short controlled irrigation run. Confirm `/Irrigation/history` contains exactly one record with the expected zone, duration, and applied amount.
5. Verify `/Irrigation/mowing-allowed` is wired as a permissive condition (watering is allowed only when its value is `1`), alongside Loxone's own mower and weather interlocks.

The scalar outputs are convenience views of the gateway's irrigation calculation. Prefer reading only those used by Loxone; use the full JSON endpoints for diagnostics rather than duplicating every reported metric as a Virtual Input.

## Development

Requires the .NET 10 SDK. Set `Gateway__AdminPassword` to at least 16 characters and `Gateway__DataDirectory` to a writable directory, then run:

```bash
dotnet run --project service
```
