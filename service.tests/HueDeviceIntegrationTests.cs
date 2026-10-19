using System.Net;
using System.Text;
using System.Text.Json;
using loxone.smart.gateway.Api.Gateway;
using loxone.smart.gateway.Api.PhilipsHue;
using Xunit;

namespace service.tests;

public sealed class HueDeviceIntegrationTests
{
    [Fact]
    public async Task Discovered_room_light_uses_utf8_middle_dot_in_display_name()
    {
        var handler = new HueDiscoveryHandler();
        var integrations = new DeviceIntegrations(new StubHttpClientFactory(handler));

        var resources = await integrations.GetHueResourcesAsync(
            new HueSettings { Id = "bridge", Host = "127.0.0.1" }, CancellationToken.None);

        var light = Assert.Single(resources, resource => resource.Type == "light");
        Assert.Equal("Lamp · Kitchen", light.Name);
        Assert.DoesNotContain("Â·", light.Name, StringComparison.Ordinal);

        var persisted = JsonSerializer.Deserialize<HueResource>(JsonSerializer.Serialize(light));
        Assert.Equal("Lamp · Kitchen", persisted?.Name);
    }

    [Fact]
    public async Task Zone_grouped_light_uses_direct_zone_device_membership_without_room_mapping()
    {
        var integrations = new DeviceIntegrations(new StubHttpClientFactory(new ZoneDiscoveryHandler()));

        var resources = await integrations.GetHueResourcesAsync(
            new HueSettings { Id = "bridge", Host = "127.0.0.1" }, CancellationToken.None);

        var lights = resources.Where(resource => resource.Type == "light").ToArray();
        Assert.Equal(2, lights.Length);
        Assert.All(lights, light => Assert.Equal("zone-1", light.ZoneId));
        Assert.All(lights, light => Assert.Null(light.RoomId));
        var zoneGroup = Assert.Single(resources, resource => resource.Type == "grouped_light");
        Assert.Equal("zone-1", zoneGroup.ZoneId);
        Assert.Null(zoneGroup.RoomId);
        Assert.Equal(new[] { "light-1", "light-2" }, zoneGroup.MemberIds);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(199999999)]
    [InlineData(200012699)]
    [InlineData(201006501)]
    public async Task Invalid_tunable_white_values_are_skipped_without_sending(int value)
    {
        var handler = new NoRequestHandler();
        var integrations = new DeviceIntegrations(new StubHttpClientFactory(handler));
        using var json = JsonDocument.Parse(value.ToString());
        var mapping = new DeviceMapping { HueLightType = PhilipsHueLightType.TUNABLE };

        var applied = await integrations.SendHueAsync(new HueSettings { Host = "127.0.0.1" }, mapping, json.RootElement, CancellationToken.None);

        Assert.False(applied);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200002700)]
    [InlineData(200012700)]
    [InlineData(201006500)]
    [InlineData(50050050)]
    public async Task Valid_tunable_white_values_are_sent(int value)
    {
        var handler = new NoRequestHandler { Response = new HttpResponseMessage(HttpStatusCode.OK) };
        var integrations = new DeviceIntegrations(new StubHttpClientFactory(handler));
        using var json = JsonDocument.Parse(value.ToString());
        var mapping = new DeviceMapping { HueLightType = PhilipsHueLightType.TUNABLE, TargetId = "target" };

        var applied = await integrations.SendHueAsync(new HueSettings { Host = "127.0.0.1" }, mapping, json.RootElement, CancellationToken.None);

        Assert.True(applied);
        Assert.Equal(1, handler.RequestCount);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class NoRequestHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpResponseMessage Response { get; init; } = new(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(Response);
        }
    }

    private sealed class HueDiscoveryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri?.AbsolutePath switch
            {
                "/clip/v2/resource/room" => """{"data":[{"id":"room-1","metadata":{"name":"Kitchen"},"children":[{"rid":"group-1","rtype":"grouped_light"}]}]}""",
                "/clip/v2/resource/zone" => """{"data":[]}""",
                "/clip/v2/resource/device" => """{"data":[{"id":"device-1","services":[{"rid":"group-1","rtype":"grouped_light"}]}]}""",
                "/clip/v2/resource/light" => """{"data":[{"id":"light-1","metadata":{"name":"Lamp"},"owner":{"rid":"device-1","rtype":"device"}}]}""",
                "/clip/v2/resource/grouped_light" => """{"data":[]}""",
                _ => "{}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ZoneDiscoveryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri?.AbsolutePath switch
            {
                "/clip/v2/resource/room" => """{"data":[]}""",
                "/clip/v2/resource/zone" => """{"data":[{"id":"zone-1","metadata":{"name":"Firepit"},"children":[{"rid":"device-1","rtype":"device"},{"rid":"device-2","rtype":"device"}]}]}""",
                "/clip/v2/resource/device" => """{"data":[{"id":"device-1","services":[{"rid":"zone-group-1","rtype":"grouped_light"}]},{"id":"device-2","services":[{"rid":"zone-group-1","rtype":"grouped_light"}]}]}""",
                "/clip/v2/resource/light" => """{"data":[{"id":"light-1","metadata":{"name":"Grill"},"owner":{"rid":"device-1","rtype":"device"}},{"id":"light-2","metadata":{"name":"Main"},"owner":{"rid":"device-2","rtype":"device"}}]}""",
                "/clip/v2/resource/grouped_light" => """{"data":[{"id":"zone-group-1","metadata":{"name":"Firepit"},"owner":{"rid":"zone-1","rtype":"zone"}}]}""",
                _ => "{}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
