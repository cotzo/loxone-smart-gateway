# Loxone Smart Gateway

A self-hosted local API bridge connecting Loxone to Philips Hue and Tuya. The web UI manages connections, mappings, and irrigation configuration. The gateway subscribes to Loxone states and dispatches provider commands through independent live queues.

## Stack

- C# / .NET 10 ASP.NET Core Web API
- Serilog and OpenTelemetry/Prometheus
- Docker multi-architecture images (ARM64 and x86-64)

## Structure

```
service/Api/Gateway/       # Live mapping runtime, queueing, state and integrations
service/Api/PhilipsHue/    # Hue command conversion and resource types
service/Api/Tuya/          # Tuya LAN protocol implementation
service/Api/Irrigation/    # Irrigation calculations, persistence and Loxone sync
service/Controllers/       # Authenticated /api gateway controller
service/wwwroot/           # Browser UI
service.tests/             # Automated tests
```

## Build and run

```bash
dotnet build service/service.csproj
dotnet test loxone.smart.gateway.sln
dotnet run --project service
docker compose up -d
```

## Architecture notes

- Provider synchronization is driven by mapped Loxone state subscriptions, not per-device legacy HTTP endpoints.
- Hue has independent paced queues per bridge and resource type; Tuya has a separate serialized queue. Queues coalesce pending state by target and retry transient failures with backoff.
- `HueCommandBuilder` converts compact mapping values into Hue CLIP command bodies; the live `DeviceIntegrations` path performs the network request.
- Irrigation weather inputs and controls are mapped to Loxone states. The service samples complete weather observations, calculates zone durations, tracks active irrigation, and updates selected Loxone controls.
- Keep encrypted gateway data and Data Protection keys together in the persistent data volume.

## HTTP surface

The application exposes authenticated `/api/...` routes for the UI and `/health` for health checks. The old unauthenticated `/PhilipsHue/...`, `/Tuya/...`, and `/Irrigation/...` routes are not supported. Prometheus metrics are exposed at `/metrics` when enabled.
