# Loxone Smart Gateway

Loxone Smart Gateway is a self-hosted local API with the purpose to bridge the gap between Loxone Ecosystem and 3rd party smart home providers. This bridge solves many limitations of the Loxone ecosystem like Custom Programming (PicoC) which does not support https requests, are limited in number of instances etc.

Please not that this bridge does not store any information and does not pass it externally.

This application exposes OpenTelemetry data that can be used by Prometheus and visualised in Grafana.

## Plugins

### 1. Philips Hue

The Philips Hue Plugin allows control of RGB, Tunable, Dim and On/Off lights through Virtual Outputs through the newly launched Hue API v2

**NOTE**:
The operations for grouped lighs are much more intensive for Hue Bridge since they are sending broadcast messages on Zigbee network. They are recommending to not have more than 1 request per second for grouped lights but from personal testing it works to have a couple of them in your instance

#### Installation

- Run the provided docker image on a RaspberryPi with the following environment variables:
  
| Env | Description |
| --- | ----------- |
| Api:PhilipsHueConfiguration:IP | The IP address of your Hue Bridge |
| Api:PhilipsHueConfiguration:AccessKey | The Access Key of your Hue Bridge |
| Configuration:EnablePrometheus | Enable or disable Prometheus metrics. Optional, default false |


- Create a new Virtual Output in Loxone and set the correct address `http://<your-raspberry-pi-ip>:<container-port>`
- Generate a Philips hue API key. [This blog entry](https://www.sitebase.be/generate-phillips-hue-api-token/) describes how to do this
- Create Virtual Outputs for each light you want to control.

| Virtual Output Parameter | Description |
| ------------------------ | ----------- |
| Command for ON           | Described further down |
| HTTP Header for ON       | `Content-Type: application/json` |
| HTTP Body for ON         | `<v>`       |
| Use as Digital Output    | `off`       |

- Configure the Command for ON URL

e.g. `/PhilipsHue/1feccf7d-3943-4450-a9c8-75c9bef4d31b?lightType=RGB&resourceType=grouped_light&transitionTime=1000`

The Light ID or LightGroup ID should be added to the path

- Query Parameters

| Name      | Description       |
| --------- | ----------------- |
| lightType | One of: RGB, TUNABLE, DIM, ONOFF |
| resourceType | One of: light, grouped_light |
| transitionTime | Fade effect duration in ms |

Please note that the light circuit in Loxone has to be set to Lumitech DMX
