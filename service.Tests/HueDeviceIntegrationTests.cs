using System.Net;
using System.Text.Json;
using loxone.smart.gateway.Api.Gateway;
using loxone.smart.gateway.Api.PhilipsHue;
using Xunit;

namespace service.tests;

public sealed class HueDeviceIntegrationTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(199999999)]
    [InlineData(200012699)]
    [InlineData(201006501)]
    public async Task Invalid_tunable_white_values_are_skipped_without_sending(int value)
    {
        var handler = new NoRequestHandler();
        var integrations = new DeviceIntegrations(hueHandlerFactory: _ => handler);
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
        var integrations = new DeviceIntegrations(hueHandlerFactory: _ => handler);
        using var json = JsonDocument.Parse(value.ToString());
        var mapping = new DeviceMapping { HueLightType = PhilipsHueLightType.TUNABLE, TargetId = "target" };

        var applied = await integrations.SendHueAsync(new HueSettings { Host = "127.0.0.1" }, mapping, json.RootElement, CancellationToken.None);

        Assert.True(applied);
        Assert.Equal(1, handler.RequestCount);
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
}
