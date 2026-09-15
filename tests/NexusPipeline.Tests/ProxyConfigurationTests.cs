using System.Net;
using NexusPipeline.Models;
using NexusPipeline.Services.Networking;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class ProxyConfigurationTests
{
    [Fact]
    public void ProxyModes_MapToExpectedHttpHandler()
    {
        using (HttpClientHandler none = ProxyConfiguration.FromSettings(new AppSettings
        {
            ProxyMode = "none",
        }).CreateHandler(OutboundHttpTarget.External, allowAutoRedirect: false))
        {
            Assert.False(none.UseProxy);
            Assert.Equal(DecompressionMethods.GZip | DecompressionMethods.Deflate, none.AutomaticDecompression);
        }

        using (HttpClientHandler system = ProxyConfiguration.FromSettings(new AppSettings
        {
            ProxyMode = "system",
        }).CreateHandler(OutboundHttpTarget.External, allowAutoRedirect: false))
        {
            Assert.True(system.UseProxy);
            Assert.Null(system.Proxy);
        }

        using (HttpClientHandler custom = ProxyConfiguration.FromSettings(new AppSettings
        {
            ProxyMode = "http",
            ProxyUrl = "http://127.0.0.1:7890",
            ProxyUsername = "user",
            ProxyPassword = "password",
        }).CreateHandler(OutboundHttpTarget.External, allowAutoRedirect: false))
        {
            Assert.True(custom.UseProxy);
            WebProxy proxy = Assert.IsType<WebProxy>(custom.Proxy);
            Assert.Equal(new Uri("http://127.0.0.1:7890"), proxy.Address);
            NetworkCredential credentials = Assert.IsType<NetworkCredential>(proxy.Credentials);
            Assert.Equal("user", credentials.UserName);
            Assert.Equal("password", credentials.Password);
        }

        using (HttpClientHandler loopback = ProxyConfiguration.FromSettings(new AppSettings
        {
            ProxyMode = "http",
            ProxyUrl = "http://127.0.0.1:7890",
        }).CreateHandler(OutboundHttpTarget.Loopback, allowAutoRedirect: false))
        {
            Assert.False(loopback.UseProxy);
        }
    }

    [Fact]
    public void CustomProxy_RequiresHttpOrHttpsAddress()
    {
        Assert.Throws<InvalidDataException>(() => ProxyConfiguration.FromSettings(new AppSettings
        {
            ProxyMode = "http",
        }));
        Assert.Throws<InvalidDataException>(() => ProxyConfiguration.FromSettings(new AppSettings
        {
            ProxyMode = "http",
            ProxyUrl = "socks5://127.0.0.1:7890",
        }));
        Assert.Throws<InvalidDataException>(() => ProxyConfiguration.FromSettings(new AppSettings
        {
            ProxyMode = "http",
            ProxyUrl = "not a uri",
        }));
    }

    [Fact]
    public void ProxyPassword_DecryptFailureIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => ProxyConfiguration.FromSettings(new AppSettings
        {
            ProxyMode = "http",
            ProxyUrl = "https://proxy.example.test:8443",
            ProxyPassword = "enc:not-valid-base64",
        }));
    }

    [Fact]
    public void InvalidMode_FallsBackToDirectConnection()
    {
        ProxyConfiguration configuration = ProxyConfiguration.FromSettings(new AppSettings
        {
            ProxyMode = "unexpected",
            ProxyUrl = "http://127.0.0.1:7890",
        });

        using HttpClientHandler handler = configuration.CreateHandler(OutboundHttpTarget.External, allowAutoRedirect: true);
        Assert.False(handler.UseProxy);
        Assert.True(handler.AllowAutoRedirect);
    }
}
