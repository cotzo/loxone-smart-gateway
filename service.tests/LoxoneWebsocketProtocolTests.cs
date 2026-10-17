using Loxone.Communicator;
using Xunit;

namespace service.tests;

public sealed class LoxoneWebsocketProtocolTests
{
    [Fact]
    public void KeepAliveHeaderHasNoPayload()
    {
        var bytes = new byte[] { 0x03, 0x06, 0, 0, 0, 0, 0, 0 };

        Assert.True(MessageHeader.TryParse(bytes, out var header));
        Assert.Equal(MessageType.KeepAlive, header.Type);
        Assert.False(WebsocketWebserviceClient.HasPayload(header));
    }

    [Fact]
    public void EmptyTextHeaderDoesNotConsumeTheFollowingFrame()
    {
        var bytes = new byte[] { 0x03, 0x00, 0, 0, 0, 0, 0, 0 };

        Assert.True(MessageHeader.TryParse(bytes, out var header));
        Assert.False(WebsocketWebserviceClient.HasPayload(header));
    }

    [Fact]
    public void NonEmptyEventTableHeaderHasPayload()
    {
        var bytes = new byte[] { 0x03, 0x02, 0, 0, 24, 0, 0, 0 };

        Assert.True(MessageHeader.TryParse(bytes, out var header));
        Assert.True(WebsocketWebserviceClient.HasPayload(header));
    }

    [Theory]
    [InlineData(false, "ws", "http")]
    [InlineData(true, "wss", "https")]
    public void TlsSettingSelectsMatchingWebsocketAndWebserviceSchemes(bool useTls, string websocketScheme, string webserviceScheme)
    {
        var websocketUri = WebsocketWebserviceClient.BuildWebsocketUri("miniserver.example.test", useTls ? 443 : 80, useTls);
        var webserviceUri = HttpWebserviceClient.BuildWebserviceUri("miniserver.example.test", useTls ? 443 : 80, useTls, "jdev/cfg/api", string.Empty);

        Assert.Equal(websocketScheme, websocketUri.Scheme);
        Assert.Equal("/ws/rfc6455", websocketUri.AbsolutePath);
        Assert.Equal(webserviceScheme, webserviceUri.Scheme);
        Assert.Equal("/jdev/cfg/api", webserviceUri.AbsolutePath);
    }
}
