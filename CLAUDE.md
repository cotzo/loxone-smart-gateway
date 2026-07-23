# Loxone Smart Gateway

A self-hosted local API bridge connecting the Loxone home automation system to third-party smart home providers (currently Philips Hue and Tuya). Runs entirely on the local network.

## Tech Stack

- **C# 14 / .NET 10.0** ASP.NET Core Web API
- **Serilog** for structured logging
- **OpenTelemetry** with Prometheus exporter for metrics
- **Docker** (multi-stage Alpine, ARM64 + x86-64)

## Project Structure

```
service/                          # Main application
  Api/PhilipsHue/                 # Hue bridge integration
    PhilipsHueMessageSender.cs    # Background queue processor (core logic)
    PhilipsHueConfiguration.cs    # Config model (IP, AccessKey)
    PhilipsHueMetrics.cs          # OpenTelemetry histogram metrics
    PhilipsHueRequestModel.cs     # Request DTO
    Enums.cs                      # LightType enum (Rgb, Tunable, Dim, OnOff)
  Api/Tuya/                       # Tuya local (LAN) integration
    TuyaMessageSender.cs          # Background queue processor
    TuyaLocalConnection.cs        # Tuya local protocol 3.4/3.5 (framing, session key, AES)
    TuyaConfiguration.cs          # Config model (device list: Name, Id, IP, LocalKey, Version)
    TuyaMetrics.cs                # OpenTelemetry histogram metrics
    TuyaRequestModel.cs           # Request DTO
  Controllers/
    PhilipsHueController.cs       # POST /PhilipsHue/{id} endpoint
    TuyaController.cs             # POST /Tuya/{name} endpoint
  HttpClientHandlerInsecure.cs    # SSL bypass for local Hue Bridge
  Program.cs                      # Startup, DI, Serilog, OpenTelemetry config
loxone.smart.gateway.sln          # Solution file
docker-compose.yaml               # Local deployment
.github/workflows/publish.yml     # CI: Docker build & push to ghcr.io
```

## Build & Run

```bash
# Build
dotnet build service/service.csproj

# Run locally (http://localhost:5009)
dotnet run --project service

# Docker
docker-compose up -d
```

## Configuration

Set via `appsettings.json`, `appsettings.Development.json`, or environment variables:

- `Api:PhilipsHueConfiguration:IP` — Hue Bridge IP (required)
- `Api:PhilipsHueConfiguration:AccessKey` — Hue API key (required)
- `Api:TuyaConfiguration:Devices:N:{Name,Id,IP,LocalKey,Version}` — Tuya device list (optional; Version is `3.4` or `3.5`, credentials come from `tinytuya wizard`/`scan`)
- `Configuration:EnablePrometheus` — Enable metrics endpoint (default: false)

## Architecture Notes

- **Queue-based processing**: Requests are enqueued via the controller and processed asynchronously by `PhilipsHueMessageSender` (a `BackgroundService`) using a `ConcurrentQueue`
- **Retry logic**: Failed Hue API calls are re-enqueued up to 10 times
- **Multi-light batching**: Semicolon-delimited IDs in a single request are split into separate queue items
- **Color conversion**: Loxone's compact integer format is decoded and converted to CIE 1931 xy chromaticity for the Hue API (includes gamma correction and D65 color space transform)
- **Insecure HTTP client**: Required because the Hue Bridge uses self-signed certificates on local network

## Endpoints

- `POST /PhilipsHue/{id}` — Enqueue a light control request (query params: `lightType`, `resourceType`, `transitionTime`)
- `POST /Tuya/{name}` — Enqueue a Tuya data point write (query param: `dp`; JSON body is the DP value and its JSON type must match the DP type)
- `GET /health` — Health check
- `GET /metrics` — Prometheus metrics (when enabled)
