using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Net.Security;
using loxone.smart.gateway.Api.PhilipsHue;
using loxone.smart.gateway.Api.Tuya;
using Serilog;

namespace loxone.smart.gateway.Api.Gateway;

public sealed class DeviceIntegrations(GatewayMetrics? metrics = null, Func<string, HttpMessageHandler>? hueHandlerFactory = null)
{
    public async Task<IReadOnlyList<HueResource>> GetHueResourcesAsync(HueSettings bridge, CancellationToken cancellationToken)
    {
        using var client = CreateHueClient(bridge);
        var resources = new List<HueResource>();
        var locations = new Dictionary<string, (string Name, string Type)>(StringComparer.OrdinalIgnoreCase);
        var membership = new Dictionary<string, (string? RoomId, string? ZoneId)>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in new[] { "room", "zone" })
        {
            using var response = await client.GetAsync($"/clip/v2/resource/{type}", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!json.RootElement.TryGetProperty("data", out var data)) continue;
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idValue)) continue;
                var locationId = idValue.GetString();
                if (string.IsNullOrWhiteSpace(locationId)) continue;
                var locationName = locationId;
                if (item.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("name", out var nameValue))
                {
                    var name = nameValue.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) locationName = name;
                }
                locations[locationId] = (locationName, type);
                if (!item.TryGetProperty("children", out var children)) continue;
                foreach (var child in children.EnumerateArray())
                {
                    if (!child.TryGetProperty("rid", out var childId)) continue;
                    var rid = childId.GetString();
                    if (string.IsNullOrWhiteSpace(rid)) continue;
                    var current = membership.GetValueOrDefault(rid);
                    membership[rid] = type == "room" ? (locationId, current.ZoneId) : (current.RoomId, locationId);
                }
            }
        }

        using (var response = await client.GetAsync("/clip/v2/resource/device", cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (json.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var device in data.EnumerateArray())
                {
                    if (!device.TryGetProperty("id", out var deviceIdValue)) continue;
                    var deviceId = deviceIdValue.GetString();
                    if (string.IsNullOrWhiteSpace(deviceId) || !device.TryGetProperty("services", out var services)) continue;
                    foreach (var service in services.EnumerateArray())
                    {
                        if (!service.TryGetProperty("rtype", out var rtype) || rtype.GetString() != "grouped_light" || !service.TryGetProperty("rid", out var groupedId)) continue;
                        var groupedRid = groupedId.GetString();
                        if (!string.IsNullOrWhiteSpace(groupedRid) && membership.TryGetValue(groupedRid, out var groupedMembership))
                            membership[deviceId] = (membership.GetValueOrDefault(deviceId).RoomId ?? groupedMembership.RoomId,
                                membership.GetValueOrDefault(deviceId).ZoneId ?? groupedMembership.ZoneId);
                    }
                }
            }
        }

        var groupedLightNumber = 0;
        foreach (var type in new[] { "light", "grouped_light" })
        {
            using var response = await client.GetAsync($"/clip/v2/resource/{type}", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!json.RootElement.TryGetProperty("data", out var data)) continue;
            foreach (var item in data.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                if (type == "grouped_light") groupedLightNumber++;
                var name = item.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("name", out var nameValue)
                    ? nameValue.GetString() : id;
                string? ownerId = null;
                string? ownerType = null;
                if (item.TryGetProperty("owner", out var owner) && owner.TryGetProperty("rid", out var ownerIdValue)) ownerId = ownerIdValue.GetString();
                if (item.TryGetProperty("owner", out owner) && owner.TryGetProperty("rtype", out var ownerTypeValue)) ownerType = ownerTypeValue.GetString();
                var membershipInfo = ownerId is not null ? membership.GetValueOrDefault(ownerId) : default;
                if (type == "grouped_light" && ownerId is not null && ownerType is "room" or "zone")
                    membershipInfo = ownerType == "room" ? (ownerId, membershipInfo.ZoneId) : (membershipInfo.RoomId, ownerId);
                if (membershipInfo.RoomId is not null && membershipInfo.ZoneId is null && membership.TryGetValue(membershipInfo.RoomId, out var roomMembership))
                    membershipInfo = (membershipInfo.RoomId, roomMembership.ZoneId);
                var room = membershipInfo.RoomId is not null ? locations.GetValueOrDefault(membershipInfo.RoomId) : default;
                var zone = membershipInfo.ZoneId is not null ? locations.GetValueOrDefault(membershipInfo.ZoneId) : default;
                if (type == "grouped_light" && ownerId is not null && locations.TryGetValue(ownerId, out var groupLocation)) name = groupLocation.Name;
                if (type == "grouped_light" && (string.IsNullOrWhiteSpace(name) || Guid.TryParse(name, out _)))
                    name = room.Name ?? zone.Name ?? $"Hue group {groupedLightNumber}";
                var displayName = name ?? id ?? string.Empty;
                if (type == "light" && room.Name is not null) displayName = $"{displayName} · {room.Name}";
                if (!string.IsNullOrWhiteSpace(id)) resources.Add(new HueResource(id, type, displayName, bridge.Id,
                    membershipInfo.RoomId, room.Name, membershipInfo.ZoneId, zone.Name));
            }
        }
        return resources;
    }

    public async Task CheckHueAsync(HueSettings bridge, CancellationToken cancellationToken)
    {
        using var client = CreateHueClient(bridge);
        using var response = await client.GetAsync("/clip/v2/resource/bridge", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> SendHueAsync(HueSettings bridge, DeviceMapping mapping, JsonElement value, CancellationToken cancellationToken, DateTimeOffset? sourceReceivedAt = null)
    {
        using var client = CreateHueClient(bridge);
        var lightType = mapping.HueLightType;
        int numeric;
        if (value.ValueKind == JsonValueKind.True)
        {
            lightType = PhilipsHueLightType.ONOFF;
            numeric = 1;
        }
        else if (value.ValueKind == JsonValueKind.False)
        {
            lightType = PhilipsHueLightType.ONOFF;
            numeric = 0;
        }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
        {
            numeric = (int)Math.Round(number);
        }
        else throw new InvalidOperationException("Hue commands require a numeric or Boolean converted value.");

        if (lightType == PhilipsHueLightType.TUNABLE && !IsValidTunableWhiteValue(numeric))
            return false;

        var command = HueCommandBuilder.BuildCommandBody(numeric, lightType, mapping.TransitionTime);
        using var content = new StringContent(command, Encoding.UTF8, "application/json");
        var sendStartedAt = DateTimeOffset.UtcNow;
        var requestDuration = Stopwatch.StartNew();
        if (sourceReceivedAt is not null)
            Log.Information("Sending Hue command for mapping {mapping} to target {targetId}; event-to-Hue-send delay {EventToSendMs:F1} ms", mapping.Name, mapping.TargetId, (sendStartedAt - sourceReceivedAt.Value).TotalMilliseconds);
        var resourceType = mapping.HueResourceType.Equals("grouped_light", StringComparison.OrdinalIgnoreCase) ? "grouped_light" : "light";
        HttpResponseMessage responseMessage;
        string responseBody;
        try
        {
            responseMessage = await client.PutAsync($"/clip/v2/resource/{mapping.HueResourceType}/{Uri.EscapeDataString(mapping.TargetId)}", content, cancellationToken);
            using (responseMessage)
                responseBody = await responseMessage.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            requestDuration.Stop();
            metrics?.RecordRequestDuration("hue", resourceType, requestDuration.Elapsed.TotalMilliseconds, "failure", bridge.Name);
            throw;
        }
        requestDuration.Stop();
        var outcome = responseMessage.IsSuccessStatusCode ? "success" : "http_error";
        metrics?.RecordRequestDuration("hue", resourceType, requestDuration.Elapsed.TotalMilliseconds, outcome, bridge.Name);
        Log.Information("Hue command response for mapping {mapping} from target {targetId}: HTTP {StatusCode}; request duration {RequestDurationMs:F1} ms", mapping.Name, mapping.TargetId, (int)responseMessage.StatusCode, requestDuration.Elapsed.TotalMilliseconds);
        if (!responseMessage.IsSuccessStatusCode)
            throw new HttpRequestException($"Hue returned {(int)responseMessage.StatusCode} ({responseMessage.StatusCode}): {responseBody}");

        // Hue CLIP v2 can return HTTP 200 while reporting a rejected command in `errors`.
        // Treat that as a failure so the mapping is retried and the actual bridge error is visible.
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            using var result = JsonDocument.Parse(responseBody);
            if (result.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                throw new InvalidOperationException($"Hue rejected the command: {errors}");
        }

        return true;
    }

    private static bool IsValidTunableWhiteValue(int value)
    {
        if (value is 0 or 50_050_050) return true;
        if (value < 200_000_000) return false;

        var brightness = (value - 200_000_000) / 10_000;
        var kelvin = value - 200_000_000 - brightness * 10_000;
        return brightness is >= 0 and <= 100 && kelvin is >= 2_700 and <= 6_500;
    }

    public async Task<IReadOnlyList<TuyaDataPoint>> GetTuyaDataPointsAsync(TuyaDeviceSettings device, CancellationToken cancellationToken)
    {
        var config = ToTuyaConfiguration(device);
        using var connection = new TuyaLocalConnection(config);
        await connection.ConnectAsync(cancellationToken);
        var points = await connection.GetDataPointsAsync(cancellationToken);
        return points.Select(x => new TuyaDataPoint(x.Key, x.Value.ValueKind.ToString(), x.Value.ToString())).ToList();
    }

    public async Task SendTuyaAsync(TuyaDeviceSettings device, int dp, JsonElement value, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Log.Information("Sending Tuya command to {device} ({ip}), DP {dp}: JSON {JsonType} {Value}", device.Name, device.IP, dp, value.ValueKind, value.GetRawText());
        try
        {
            using var connection = new TuyaLocalConnection(ToTuyaConfiguration(device));
            await connection.ConnectAsync(cancellationToken);
            await connection.SetDataPointAsync(dp, value, cancellationToken);
            stopwatch.Stop();
            metrics?.RecordRequestDuration("tuya", "device", stopwatch.Elapsed.TotalMilliseconds, "success");
            Log.Information("Tuya command send completed for {device} DP {dp} in {DurationMs:F1} ms; acknowledgement is best-effort", device.Name, dp, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch
        {
            stopwatch.Stop();
            metrics?.RecordRequestDuration("tuya", "device", stopwatch.Elapsed.TotalMilliseconds, "failure");
            throw;
        }
    }

    private HttpClient CreateHueClient(HueSettings bridge)
    {
        var host = bridge.Host.Contains("://", StringComparison.Ordinal) ? bridge.Host : $"https://{bridge.Host}";
        var baseAddress = new Uri(host.TrimEnd('/') + "/");
        var handler = hueHandlerFactory?.Invoke(baseAddress.DnsSafeHost) ?? CreateHueHandler(baseAddress.DnsSafeHost);
        var client = new HttpClient(handler, disposeHandler: true);
        client.BaseAddress = baseAddress;
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.TryAddWithoutValidation("hue-application-key", bridge.ApplicationKey);
        return client;
    }

    private static HttpMessageHandler CreateHueHandler(string configuredHost) => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
            string.Equals(request.RequestUri?.DnsSafeHost, configuredHost, StringComparison.OrdinalIgnoreCase)
            && errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors
    };

    private static TuyaDeviceConfiguration ToTuyaConfiguration(TuyaDeviceSettings device) => new()
    {
        Name = device.Name,
        Id = device.DeviceId,
        IP = device.IP,
        LocalKey = device.LocalKey,
        Version = device.Version
    };
}
