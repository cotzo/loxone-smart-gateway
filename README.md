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
| `Api:TuyaConfiguration:Devices:0:Name` | Friendly name used in the request URL | *(optional)* |
| `Api:TuyaConfiguration:Devices:0:Id` | Tuya device ID | *(optional)* |
| `Api:TuyaConfiguration:Devices:0:IP` | Device IP address on the LAN | *(optional)* |
| `Api:TuyaConfiguration:Devices:0:LocalKey` | Device local key | *(optional)* |
| `Api:TuyaConfiguration:Devices:0:Version` | Tuya local protocol version (`3.4` or `3.5`) | *(optional)* |
| `Configuration:EnablePrometheus` | Enable Prometheus metrics endpoint | `false` |

To generate a Philips Hue API key, follow [this guide](https://www.sitebase.be/generate-phillips-hue-api-token/).

Repeat the `Devices:0` block with `Devices:1`, `Devices:2`, ... for additional Tuya devices.

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

### Tuya

Controls Tuya WiFi devices (fans, lights, switches, ...) directly over the local network — no Tuya cloud involved at runtime. Supports local protocol versions 3.4 and 3.5.

Each command sets one data point (DP) on a device. DPs are device-specific; a ceiling fan with light typically exposes `1` (fan on/off), `3` (fan speed), `8` (direction) and `15` (light on/off).

#### Obtaining device credentials

Use [tinytuya](https://github.com/jasonacox/tinytuya) once, at setup time:

```bash
pipx install tinytuya
tinytuya wizard   # pulls device IDs and local keys from your Tuya IoT cloud project
tinytuya scan     # shows each device's IP and protocol version
```

**NOTE**: The local key changes if the device is removed and re-paired in the Smart Life app — rerun the wizard if commands stop working after re-pairing.

#### Loxone Setup

1. Use the same **Virtual Output** as for Philips Hue (`http://<gateway-ip>:8080`).

2. Create a **Virtual Output Command** per action, with `HTTP Header for ON` set to `Content-Type: application/json`:

| Action | Command for ON | HTTP Body for ON |
| --- | --- | --- |
| Fan on/off | `/Tuya/fan?dp=1` | `true` / `false` |
| Fan speed (analog) | `/Tuya/fan?dp=3` | `<v>` |
| Light on/off | `/Tuya/fan?dp=15` | `true` / `false` |
| Direction | `/Tuya/fan?dp=8` | `"forward"` / `"reverse"` |

The body is sent to the device as-is, so its JSON type must match the DP type: `true`/`false` for Boolean DPs, a bare number for Integer DPs, a quoted string for Enum DPs.

## API Endpoints

| Method | Path | Description |
| --- | --- | --- |
| `POST` | `/PhilipsHue/{id}` | Enqueue a light control request |
| `POST` | `/Tuya/{name}` | Enqueue a Tuya data point write (query param: `dp`) |
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
