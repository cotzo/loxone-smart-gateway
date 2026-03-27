# Loxone Smart Gateway

[![HitCount](https://hits.dwyl.com/cotzo/loxone-smart-gateway.svg?style=flat-square)](http://hits.dwyl.com/cotzo/loxone-smart-gateway)

Loxone Smart Gateway is a self-hosted local API that bridges the gap between the Loxone Ecosystem and 3rd party smart home providers. It solves many limitations of the Loxone ecosystem — Custom Programming (PicoC) does not support HTTPS requests and is limited in the number of instances, among other constraints.

This bridge does not store any information and does not pass data externally. It exposes OpenTelemetry metrics that can be scraped by Prometheus and visualised in Grafana.

## Quick Start

```yaml
# docker-compose.yaml
version: '3'
services:
  loxone-smart-gateway:
    image: ghcr.io/cotzo/loxone-smart-gateway:latest
    restart: always
    ports:
      - 8080:8080
    environment:
      - Api:PhilipsHueConfiguration:IP=<your-bridge-ip>
      - Api:PhilipsHueConfiguration:AccessKey=<your-access-key>
```

```bash
docker-compose up -d
```

## Configuration

| Environment Variable | Description | Default |
| --- | --- | --- |
| `Api:PhilipsHueConfiguration:IP` | IP address of your Hue Bridge | *(required)* |
| `Api:PhilipsHueConfiguration:AccessKey` | Access Key for the Hue Bridge API | *(required)* |
| `Configuration:EnablePrometheus` | Enable Prometheus metrics endpoint | `false` |

To generate a Philips Hue API key, follow [this guide](https://www.sitebase.be/generate-phillips-hue-api-token/).

## Plugins

### Philips Hue

Controls RGB, Tunable, Dim and On/Off lights through Loxone Virtual Outputs using the Hue API v2.

**NOTE**: Operations for grouped lights are more intensive for the Hue Bridge since they broadcast on the Zigbee network. Philips recommends no more than 1 request per second for grouped lights, though in practice a few concurrent requests tend to work.

#### Loxone Setup

1. Create a new **Virtual Output** in Loxone Config and set the address to `http://<gateway-ip>:8080`. Set `Close connection after sending` to `on` and clear the `Separator` field.

2. Create **Virtual Output Commands** for each light you want to control:

| Virtual Output Parameter | Value |
| --- | --- |
| Command for ON | See URL format below |
| HTTP Header for ON | `Content-Type: application/json` |
| HTTP Body for ON | `<v>` |
| Use as Digital Output | `off` |

3. Set the **Command for ON** URL:

```
/PhilipsHue/<light-id>?lightType=RGB&resourceType=grouped_light&transitionTime=1000
```

Replace `<light-id>` with the Hue Light ID or Light Group ID.

#### Query Parameters

| Name | Description | Values |
| --- | --- | --- |
| `lightType` | Type of light being controlled | `RGB`, `TUNABLE`, `DIM`, `ONOFF` |
| `resourceType` | Hue resource type | `light`, `grouped_light` |
| `transitionTime` | Fade effect duration in milliseconds | Integer |

The light circuit in Loxone must be set to **Lumitech DMX**.

## API Endpoints

| Method | Path | Description |
| --- | --- | --- |
| `POST` | `/PhilipsHue/{id}` | Enqueue a light control request |
| `GET` | `/health` | Health check |
| `GET` | `/metrics` | Prometheus metrics (when enabled) |

## Building from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
# Build
dotnet build service/service.csproj

# Run locally (http://localhost:5009)
dotnet run --project service

# Build Docker image
docker build -f service/Dockerfile -t loxone-smart-gateway service/
```
