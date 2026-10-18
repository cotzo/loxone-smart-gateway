using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Loxone.Communicator;
using Loxone.Communicator.Events;
using Serilog;

namespace loxone.smart.gateway.Api.Gateway;

public sealed class LoxoneIntegration
{
    public async Task<IReadOnlyList<LoxoneStateOption>> DiscoverAsync(LoxoneSettings server, CancellationToken cancellationToken)
    {
        using var client = new WebsocketWebserviceClient(server.Host, server.Port, 2, Guid.NewGuid().ToString(), "Loxone Smart Gateway", server.UseTls, server.CertificateAuthorityPem);
        // LxCommunicator 0.9.5 invokes this event without checking for subscribers.
        client.OnAuthenticated += (_, _) => { };
        await PrimePublicKeyAsync(client);
        using var tokenHandler = new TokenHandler(client, server.Username);
        tokenHandler.SetPassword(server.Password);
        await AuthenticateAsync(client, tokenHandler);
        var structureRequest = new StructureFileRequest()
        {
            Timeout = 30_000
        };
        var response = await client.SendWebservice(structureRequest);
        if (response is null)
            throw new TimeoutException("Timed out waiting for the Miniserver structure file after 30 seconds.");
        if (string.IsNullOrWhiteSpace(response.Value))
            throw new InvalidOperationException("The Miniserver returned an empty structure file.");
        using var json = JsonDocument.Parse(response.Value);
        var options = new List<LoxoneStateOption>();
        if (json.RootElement.TryGetProperty("controls", out var controls))
            ReadControls(controls, string.Empty, options, server.Id);
        return options.OrderBy(x => x.Control).ThenBy(x => x.State).ToList();
    }

    public async Task CheckAsync(LoxoneSettings server, CancellationToken cancellationToken) =>
        _ = await DiscoverAsync(server, cancellationToken);

    public async Task RunSessionAsync(LoxoneSettings server, Action<string, string> onValue, Action<string> status,
        IReadOnlySet<string> watchedStateUuids, CancellationToken cancellationToken, int sessionAttempt = 1)
    {
        var sessionLog = Log.ForContext("LoxoneServer", server.Name).ForContext("SessionAttempt", sessionAttempt);
        var sessionStartedAt = DateTimeOffset.UtcNow;
        using var client = new WebsocketWebserviceClient(server.Host, server.Port, 2, Guid.NewGuid().ToString(), "Loxone Smart Gateway", server.UseTls, server.CertificateAuthorityPem);
        // LxCommunicator 0.9.5 invokes this event without checking for subscribers.
        client.OnAuthenticated += (_, _) => { };
        await PrimePublicKeyAsync(client);
        using var tokenHandler = new TokenHandler(client, server.Username);
        tokenHandler.SetPassword(server.Password);
        var eventTableCount = 0;
        long lastEventTableUnixMs = 0;
        client.OnReceiveEventTable += (_, args) =>
        {
            var tableNumber = Interlocked.Increment(ref eventTableCount);
            Interlocked.Exchange(ref lastEventTableUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (tableNumber <= 3)
            {
                var sampleStates = args.States.Take(3).Select(state =>
                {
                    var members = state.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Select(property => property.Name)
                        .Concat(state.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Select(field => field.Name));
                    return $"{state.GetType().Name} members=[{string.Join(",", members)}] uuid={ReadProperty(state, "Uuid", "UUID", "Id")}";
                });
                sessionLog.Information("Loxone event table {tableNumber} received with {stateCount} states; sample: {sampleStates}",
                    tableNumber, args.States.Count(), string.Join("; ", sampleStates));
            }

            foreach (var state in args.States)
            {
                var uuid = ReadProperty(state, "Uuid", "UUID", "Id")?.ToString();
                if (string.IsNullOrWhiteSpace(uuid) || !watchedStateUuids.Contains(NormalizeUuid(uuid))) continue;

                var raw = ReadProperty(state, "Value", "DVal", "StateValue", "Text");
                var members = state.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(property => $"{property.Name}={property.GetValue(state) ?? "null"}")
                    .Concat(state.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)
                        .Select(field => $"{field.Name}={field.GetValue(state) ?? "null"}"));
                sessionLog.Information("Loxone mapped state payload {uuid}: {stateType} {members}", uuid, state.GetType().FullName, string.Join(", ", members));
                if (raw is null) continue;
                onValue(uuid, raw.ToString() ?? string.Empty);
            }
        };
        client.OnConnectionError += exception =>
            sessionLog.Warning(exception, "Loxone websocket listener failed for {server}", server.Name);
        await AuthenticateAsync(client, tokenHandler);
        var response = await client.SendWebservice(new WebserviceRequest<string>("jdev/sps/enablebinstatusupdate", EncryptionType.None));
        if (response is null) throw new IOException("Miniserver closed the state subscription during setup.");
        status("Connected; receiving live state");
        sessionLog.Information("Loxone state subscription active after {SetupDurationMs:F1} ms", (DateTimeOffset.UtcNow - sessionStartedAt).TotalMilliseconds);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var keepAliveDelay = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                var completed = await Task.WhenAny(keepAliveDelay, client.ConnectionFailure);
                if (completed == client.ConnectionFailure)
                {
                    var listenerError = await client.ConnectionFailure;
                    throw new IOException($"Miniserver WebSocket listener stopped: {listenerError.Message}", listenerError);
                }
                await keepAliveDelay;
                await client.KeepAliveAsync(TimeSpan.FromSeconds(10), cancellationToken);

                var lastEventMs = Interlocked.Read(ref lastEventTableUnixMs);
                sessionLog.Information(
                    "Loxone event-stream heartbeat for {server}: {eventTableCount} event tables received; last event table {lastEventTable}; keepalive acknowledged; session uptime {SessionUptimeSeconds:F1} s",
                    server.Name,
                    Volatile.Read(ref eventTableCount),
                    lastEventMs == 0 ? "not received" : DateTimeOffset.FromUnixTimeMilliseconds(lastEventMs).ToString("O"),
                    (DateTimeOffset.UtcNow - sessionStartedAt).TotalSeconds);
            }
        }
        finally { status("Disconnected"); }
    }

    private static object? ReadProperty(object instance, params string[] candidates)
    {
        var type = instance.GetType();
        foreach (var candidate in candidates)
        {
            var property = type.GetProperty(candidate, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is not null) return property.GetValue(instance);
            var field = type.GetField(candidate, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field is not null) return field.GetValue(instance);
        }
        return null;
    }

    private static string NormalizeUuid(string uuid) => uuid.Replace("-", string.Empty, StringComparison.Ordinal);

    internal static bool TryParseStateValue(string? raw, out double value)
    {
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value))
            return true;

        value = 0;
        if (TryParseHsv(raw, out value))
            return true;

        if (string.IsNullOrWhiteSpace(raw) || !raw.StartsWith("temp(", StringComparison.OrdinalIgnoreCase) || !raw.EndsWith(')'))
            return false;

        var components = raw[5..^1].Split(',');
        if (components.Length != 2 ||
            !double.TryParse(components[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var brightness) ||
            !double.TryParse(components[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var kelvin) ||
            brightness is < 0 or > 100 || kelvin <= 0)
            return false;

        // PhilipsHueMessageSender's TUNABLE format is 200000000 + brightness*10000 + Kelvin.
        value = 200_000_000 + Math.Round(brightness) * 10_000 + Math.Round(kelvin);
        return true;
    }

    private static bool TryParseHsv(string? raw, out double packedRgb)
    {
        packedRgb = 0;
        if (string.IsNullOrWhiteSpace(raw) ||
            !raw.StartsWith("hsv(", StringComparison.OrdinalIgnoreCase) ||
            !raw.EndsWith(')'))
            return false;

        var components = raw[4..^1].Split(',');
        if (components.Length != 3 ||
            !double.TryParse(components[0].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var hue) ||
            !double.TryParse(components[1].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var saturation) ||
            !double.TryParse(components[2].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var brightness) ||
            !double.IsFinite(hue) || !double.IsFinite(saturation) || !double.IsFinite(brightness) ||
            hue is < 0 or > 360 || saturation is < 0 or > 100 || brightness is < 0 or > 100)
            return false;

        // Convert HSV (hue in degrees; saturation/value in percent) into the
        // packed BGR-percentage value consumed by PhilipsHueMessageSender.
        var chroma = brightness / 100 * saturation / 100;
        var hueSector = (hue % 360) / 60;
        var secondary = chroma * (1 - Math.Abs(hueSector % 2 - 1));
        var (red, green, blue) = (int)hueSector switch
        {
            0 => (chroma, secondary, 0d),
            1 => (secondary, chroma, 0d),
            2 => (0d, chroma, secondary),
            3 => (0d, secondary, chroma),
            4 => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary)
        };
        var match = brightness / 100 - chroma;
        var redPercent = (int)Math.Round((red + match) * 100, MidpointRounding.AwayFromZero);
        var greenPercent = (int)Math.Round((green + match) * 100, MidpointRounding.AwayFromZero);
        var bluePercent = (int)Math.Round((blue + match) * 100, MidpointRounding.AwayFromZero);
        packedRgb = bluePercent * 1_000_000d + greenPercent * 1_000d + redPercent;
        return true;
    }

    private static async Task AuthenticateAsync(WebsocketWebserviceClient client, TokenHandler tokenHandler)
    {
        try { await client.Authenticate(tokenHandler); }
        catch (Exception ex) when (ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Loxone rejected the login. Verify the configured Miniserver username and password.", ex);
        }
    }

    private static async Task PrimePublicKeyAsync(WebsocketWebserviceClient client)
    {
        // LxCommunicator 0.9.5 converts the Miniserver's public key using
        // DotNetUtilities.ToRSA(..., CspParameters), which only works on Windows.
        // Pre-populating its public Session.PublicKey property avoids that CAPI path;
        // RSA.ImportFromPem and RSAParameters are supported on macOS and Linux too.
        var response = await client.HttpClient.SendWebservice(new WebserviceRequest<string>("jdev/sys/getPublicKey", EncryptionType.None)
        {
            NeedAuthentication = false
        });
        var pem = response?.Value;
        if (string.IsNullOrWhiteSpace(pem))
            throw new InvalidOperationException("Miniserver did not return its public key for authentication.");

        pem = pem.Replace("-----BEGIN CERTIFICATE-----", "-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal)
                 .Replace("-----END CERTIFICATE-----", "-----END PUBLIC KEY-----", StringComparison.Ordinal);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var key = rsa.ExportParameters(false);
        var xmlKey = new XElement("RSAKeyValue",
            new XElement("Modulus", Convert.ToBase64String(key.Modulus!)),
            new XElement("Exponent", Convert.ToBase64String(key.Exponent!)))
            .ToString(SaveOptions.DisableFormatting);

        var publicKeyProperty = client.Session.GetType().GetProperty("PublicKey", BindingFlags.Public | BindingFlags.Instance);
        if (publicKeyProperty?.GetSetMethod(nonPublic: true) is null)
            throw new InvalidOperationException("The Loxone communicator public-key hook is unavailable.");
        publicKeyProperty.SetValue(client.Session, xmlKey);
    }

    private static void ReadControls(JsonElement controls, string parent, List<LoxoneStateOption> result, string serverId)
    {
        if (controls.ValueKind != JsonValueKind.Object) return;
        foreach (var control in controls.EnumerateObject())
        {
            if (control.Value.ValueKind != JsonValueKind.Object) continue;
            var item = control.Value;
            var name = item.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? control.Name : control.Name;
            var path = string.IsNullOrEmpty(parent) ? name : $"{parent} / {name}";
            var type = item.TryGetProperty("type", out var typeNode) ? typeNode.GetString() ?? "" : "";
            if (item.TryGetProperty("states", out var states) && states.ValueKind == JsonValueKind.Object)
            {
                foreach (var state in states.EnumerateObject())
                {
                    var uuid = state.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(uuid)) result.Add(new LoxoneStateOption(uuid, path, type, state.Name, serverId));
                }
            }
            if (item.TryGetProperty("subControls", out var subControls)) ReadControls(subControls, path, result, serverId);
        }
    }

    private sealed class StructureFileRequest : WebserviceRequest<string>
    {
        public StructureFileRequest() : base("data/LoxAPP3.json", EncryptionType.None) { }

        public override bool TryValidateResponse(WebserviceResponse response)
        {
            var raw = response.GetAsStringContent();
            try
            {
                using var document = JsonDocument.Parse(raw);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    !document.RootElement.TryGetProperty("LL", out _))
                {
                    // Loxone sends structure files as raw text messages, unlike the
                    // LL-wrapped JSON envelope expected by LxCommunicator requests.
                    var envelope = JsonSerializer.Serialize(new
                    {
                        LL = new { control = Command, Code = 200, value = raw }
                    });
                    var adapted = new WebserviceResponse(
                        response.Header,
                        Encoding.UTF8.GetBytes(envelope),
                        response.ClientCode);
                    return base.TryValidateResponse(adapted);
                }
            }
            catch (JsonException)
            {
                // Let the library preserve its normal error handling for non-JSON responses.
            }
            return base.TryValidateResponse(response);
        }
    }
}
