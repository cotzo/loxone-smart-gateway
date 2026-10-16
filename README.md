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
5. Configure irrigation coordinates, model parameters and zones in the Irrigation Configuration section. Each zone needs an ID (used by `/Irrigation/zone/{id}`), type, exposure multiplier and measured application rate in mm/hour.

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

The existing `/Irrigation` routes remain for weather ingestion and irrigation results, including `/Irrigation/weather/{field}/{value}`, `/Irrigation/weather/commit`, `/Irrigation/zone/{id}`, and scalar outputs. `/health` is available for container health checks. Prometheus metrics are available at `/metrics` when `Configuration__EnablePrometheus=true`.

## Development

Requires the .NET 10 SDK. Set `Gateway__AdminPassword` to at least 16 characters and `Gateway__DataDirectory` to a writable directory, then run:

```bash
dotnet run --project service
```
