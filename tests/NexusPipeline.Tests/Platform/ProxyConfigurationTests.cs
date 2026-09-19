using System.Net;
using Xunit;
using NexusPipeline.Host.Composition.Adapters;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Platform.Networking;

namespace NexusPipeline.Tests.Platform;

public sealed class ProxyConfigurationTests
{
    [Fact]
    public void ProxyModes_MapToExpectedHttpHandler()
    {
        using (HttpClientHandler none = ProxyConfiguration.FromOptions(new OutboundProxyOptions("none", "", "", ""))
            .CreateHandler(OutboundHttpTarget.External, allowAutoRedirect: false))
        {
            Assert.False(none.UseProxy);
            Assert.Equal(System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate, none.AutomaticDecompression);
        }

        using (HttpClientHandler system = ProxyConfiguration.FromOptions(new OutboundProxyOptions("system", "", "", ""))
            .CreateHandler(OutboundHttpTarget.External, allowAutoRedirect: false))
        {
            Assert.True(system.UseProxy);
            Assert.Null(system.Proxy);
        }

        using (HttpClientHandler custom = ProxyConfiguration.FromOptions(new OutboundProxyOptions(
            "http", "http://127.0.0.1:7890", "user", "password"))
            .CreateHandler(OutboundHttpTarget.External, allowAutoRedirect: false))
        {
            Assert.True(custom.UseProxy);
            WebProxy proxy = Assert.IsType<WebProxy>(custom.Proxy);
            Assert.Equal(new Uri("http://127.0.0.1:7890"), proxy.Address);
            NetworkCredential credentials = Assert.IsType<NetworkCredential>(proxy.Credentials);
            Assert.Equal("user", credentials.UserName);
            Assert.Equal("password", credentials.Password);
        }

        using (HttpClientHandler loopback = ProxyConfiguration.FromOptions(new OutboundProxyOptions(
            "http", "http://127.0.0.1:7890", "", ""))
            .CreateHandler(OutboundHttpTarget.Loopback, allowAutoRedirect: false))
        {
            Assert.False(loopback.UseProxy);
        }
    }

    [Fact]
    public void CustomProxy_RequiresHttpOrHttpsAddress()
    {
        Assert.Throws<InvalidDataException>(() => ProxyConfiguration.FromOptions(new OutboundProxyOptions("http", "", "", "")));
        Assert.Throws<InvalidDataException>(() => ProxyConfiguration.FromOptions(new OutboundProxyOptions("http", "socks5://127.0.0.1:7890", "", "")));
        Assert.Throws<InvalidDataException>(() => ProxyConfiguration.FromOptions(new OutboundProxyOptions("http", "not a uri", "", "")));
    }

    [Fact]
    public void ProxyPassword_DecryptFailureIsRejectedByHostProjection()
    {
        Assert.Throws<InvalidDataException>(() => OutboundProxyOptionsAdapter.FromSettings(new AppSettings
        {
            ProxyMode = "http",
            ProxyUrl = "https://proxy.example.test:8443",
            ProxyPassword = "enc:not-valid-base64",
        }));
    }

    [Fact]
    public void InvalidMode_FallsBackToDirectConnection()
    {
        ProxyConfiguration configuration = ProxyConfiguration.FromOptions(new OutboundProxyOptions(
            "unexpected", "http://127.0.0.1:7890", "", ""));

        using HttpClientHandler handler = configuration.CreateHandler(OutboundHttpTarget.External, allowAutoRedirect: true);
        Assert.False(handler.UseProxy);
        Assert.True(handler.AllowAutoRedirect);
    }
}
