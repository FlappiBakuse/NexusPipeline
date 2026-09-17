using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>更新域 L1：受限版本解析比较、releases JSON 解析、渠道过滤、主机白名单与当前 zip 合约。</summary>
public sealed class UpdateCatalogTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("v0.10.0", "0.10.0")]
    [InlineData("10.11.12", "10.11.12")]
    [InlineData("v1.2.3-beta.2", "1.2.3-beta.2")]
    [InlineData("1.2.3-rc.1", "1.2.3-rc.1")]
    public void TryParseTag_AcceptsStandardTags(string tag, string expected)
    {
        Assert.True(UpdateCatalog.TryParseTag(tag, out var version));
        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("v1.2.3.4")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseTag_RejectsMalformedTags(string? tag)
    {
        Assert.False(UpdateCatalog.TryParseTag(tag, out _));
    }

    [Fact]
    public void Compare_OrdersByMajorThenMinorThenPatch()
    {
        Assert.True(UpdateCatalog.Compare(V("0.10.0"), V("1.0.0")) < 0);
        Assert.True(UpdateCatalog.Compare(V("1.2.3"), V("1.2.3")) == 0);
        Assert.True(UpdateCatalog.Compare(V("1.2.4"), V("1.2.3")) > 0);
        Assert.True(UpdateCatalog.Compare(V("1.3.0"), V("1.2.9")) > 0);
        Assert.True(UpdateCatalog.Compare(V("1.2.3-beta.2"), V("1.2.3-rc.1")) < 0);
        Assert.True(UpdateCatalog.Compare(V("1.2.3-rc.1"), V("1.2.3")) < 0);
    }

    [Fact]
    public void PickRelease_SkipsDraftAndRequiresBothAssets()
    {
        // 当前版本 0.10.0；v0.10.1 资产齐全 → 选中；v0.10.2 缺少 sha → 跳过；v0.10.3 draft → 跳过。
        JsonNode root = JsonNode.Parse("""
        [
          { "tag_name": "v0.10.2-beta.1", "draft": false, "prerelease": true, "assets": [
              { "name": "NexusPipeline-v0.10.2-beta.1-win-x64.zip", "browser_download_url": "https://github.com/x.zip" }
          ] },
          { "tag_name": "v0.10.3-beta.1", "draft": true, "prerelease": true, "assets": [
              { "name": "NexusPipeline-v0.10.3-beta.1-win-x64.zip", "browser_download_url": "https://github.com/x.zip" },
              { "name": "NexusPipeline-v0.10.3-beta.1-win-x64.zip.sha256", "browser_download_url": "https://github.com/x.sha" }
          ] },
          { "tag_name": "v0.10.1-beta.1", "draft": false, "prerelease": true, "body": "更新说明",
            "assets": [
              { "name": "NexusPipeline-v0.10.1-beta.1-win-x64.zip", "browser_download_url": "https://github.com/a.zip" },
              { "name": "NexusPipeline-v0.10.1-beta.1-win-x64.zip.sha256", "browser_download_url": "https://github.com/a.sha" }
          ] }
        ]
        """)!;

        ReleaseInfo? release = UpdateCatalog.PickRelease(root, "prerelease", V("0.10.0"));

        Assert.NotNull(release);
        Assert.Equal("v0.10.1-beta.1", release!.Tag);
        Assert.Equal("0.10.1-beta.1", release.VersionText);
        Assert.Equal("更新说明", release.Notes);
        Assert.True(release.Prerelease);
    }

    [Fact]
    public void PickRelease_StableChannelFiltersPrerelease()
    {
        JsonNode root = JsonNode.Parse("""
        [
          { "tag_name": "v1.0.0", "draft": false, "prerelease": false, "assets": [
              { "name": "NexusPipeline-v1.0.0-win-x64.zip", "browser_download_url": "https://github.com/s.zip" },
              { "name": "NexusPipeline-v1.0.0-win-x64.zip.sha256", "browser_download_url": "https://github.com/s.sha" }
          ] },
          { "tag_name": "v0.10.9", "draft": false, "prerelease": true, "assets": [
              { "name": "NexusPipeline-v0.10.9-win-x64.zip", "browser_download_url": "https://github.com/p.zip" },
              { "name": "NexusPipeline-v0.10.9-win-x64.zip.sha256", "browser_download_url": "https://github.com/p.sha" }
          ] }
        ]
        """)!;

        ReleaseInfo? stable = UpdateCatalog.PickRelease(root, "stable", V("0.10.0"));
        ReleaseInfo? prerelease = UpdateCatalog.PickRelease(root, "prerelease", V("0.10.0"));

        Assert.Equal("v1.0.0", stable!.Tag);
        // prerelease 渠道取最高版本（stable 与 prerelease 均可见）。
        Assert.Equal("v1.0.0", prerelease!.Tag);
    }

    [Theory]
    [InlineData("v1.0.0", false, true)]
    [InlineData("v1.0.0", true, false)]
    [InlineData("v0.15.12", true, true)]
    [InlineData("v0.15.12", false, false)]
    [InlineData("v1.0.0-beta.1", true, true)]
    [InlineData("v1.0.0-beta.1", false, false)]
    [InlineData("v1.0.0-rc.1", true, true)]
    [InlineData("v1.0.0-rc.1", false, false)]
    public void PickRelease_RequiresConsistentPrereleaseMetadata(string tag, bool prerelease, bool expected)
    {
        string version = tag[1..];
        JsonNode root = new JsonArray
        {
            new JsonObject
            {
                ["tag_name"] = tag,
                ["draft"] = false,
                ["prerelease"] = prerelease,
                ["assets"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = $"NexusPipeline-v{version}-win-x64.zip",
                        ["browser_download_url"] = "https://github.com/package.zip",
                    },
                    new JsonObject
                    {
                        ["name"] = $"NexusPipeline-v{version}-win-x64.zip.sha256",
                        ["browser_download_url"] = "https://github.com/package.sha256",
                    },
                },
            },
        };

        ReleaseInfo? release = UpdateCatalog.PickRelease(root, "prerelease", V("0.0.0"));

        Assert.Equal(expected, release is not null);
        if (release is not null)
        {
            Assert.Equal(prerelease, release.Prerelease);
        }
    }

    [Fact]
    public void PickRelease_ReturnsNullWhenLatestNotAboveCurrent()
    {
        JsonNode root = JsonNode.Parse("""
        [
          { "tag_name": "v1.0.0", "draft": false, "prerelease": false, "assets": [
              { "name": "NexusPipeline-v1.0.0-win-x64.zip", "browser_download_url": "https://github.com/x.zip" },
              { "name": "NexusPipeline-v1.0.0-win-x64.zip.sha256", "browser_download_url": "https://github.com/x.sha" }
          ] }
        ]
        """)!;

        Assert.Null(UpdateCatalog.PickRelease(root, "prerelease", V("1.0.0")));
        Assert.Null(UpdateCatalog.PickRelease(JsonNode.Parse("[]"), "prerelease", V("1.0.0")));
    }

    [Fact]
    public void IsAllowedHost_DefaultSourceAllowsGitHubOnly()
    {
        Assert.True(UpdateCatalog.IsAllowedHost("api.github.com", null));
        Assert.True(UpdateCatalog.IsAllowedHost("github.com", null));
        Assert.False(UpdateCatalog.IsAllowedHost("evil.example.com", null));
    }

    [Fact]
    public void IsAllowedHost_CustomSourceAllowsOnlyItsOwnHost()
    {
        string source = "https://mirror.example.com/updates/releases";
        Assert.True(UpdateCatalog.IsAllowedHost("mirror.example.com", source));
        Assert.False(UpdateCatalog.IsAllowedHost("github.com", source));
        Assert.False(UpdateCatalog.IsAllowedHost("evil.example.com", source));
        // 测试镜像（回环 http）
        Assert.True(UpdateCatalog.IsAllowedHost("127.0.0.1", "http://127.0.0.1:5899/releases"));
    }

    [Theory]
    [InlineData("https://api.github.com/x", null)]
    [InlineData("http://127.0.0.1:5899/x", null)]
    public void ValidateSource_AcceptsHttpsAndLoopbackHttp(string source, object? _)
    {
        Assert.Null(UpdateCatalog.ValidateSource(source));
    }

    [Fact]
    public void ValidateSource_RejectsNonHttpsRemoteAndGarbage()
    {
        Assert.NotNull(UpdateCatalog.ValidateSource("http://example.com/releases"));
        Assert.NotNull(UpdateCatalog.ValidateSource("not a url"));
        Assert.NotNull(UpdateCatalog.ValidateSource("ftp://example.com/x"));
    }

    [Fact]
    public async Task SourcePolicy_RejectsAssetSchemeAndRedirectEscape()
    {
        UpdateSourcePolicy policy = new("https://mirror.example.com/releases");

        Assert.Null(policy.ValidateAssetUri(new Uri("https://mirror.example.com/releases/pkg.zip")));
        Assert.NotNull(policy.ValidateAssetUri(new Uri("http://mirror.example.com/releases/pkg.zip")));
        Assert.NotNull(policy.ValidateAssetUri(new Uri("https://evil.example.com/pkg.zip")));

        using var http = new HttpClient(new RedirectEscapeHandler());
        await Assert.ThrowsAsync<InvalidDataException>(() => policy.GetAsync(
            http,
            new Uri("https://mirror.example.com/releases/pkg.zip"),
            UpdateResourceKind.ReleaseAsset,
            "test",
            CancellationToken.None));
    }

    [Fact]
    public async Task SourcePolicy_PolicyUsesItsOwnUriBoundary()
    {
        var defaultPolicy = new UpdateSourcePolicy("");
        Assert.Null(defaultPolicy.ValidatePolicyUri(new Uri(UpdatePolicy.DefaultPolicyUrl)));
        Assert.NotNull(defaultPolicy.ValidatePolicyUri(new Uri(
            "https://github.com/FlappiBakuse/NexusPipeline/releases/download/v0.17.0/update-policy.json")));
        Assert.NotNull(defaultPolicy.ValidatePolicyUri(new Uri(
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline/main/update-policy.json?source=mirror")));

        var customPolicy = new UpdateSourcePolicy("https://mirror.example.com/releases");
        Assert.Null(customPolicy.ValidatePolicyUri(UpdatePolicy.ResolveUri(customPolicy)));
        Assert.NotNull(customPolicy.ValidatePolicyUri(new Uri("https://other.example.com/update-policy.json")));
        Assert.NotNull(customPolicy.ValidatePolicyUri(new Uri("http://mirror.example.com/update-policy.json")));

        using var http = new HttpClient(new RedirectEscapeHandler(new Uri(
            "https://github.com/FlappiBakuse/NexusPipeline/releases/download/v0.17.0/update-policy.json")));
        await Assert.ThrowsAsync<InvalidDataException>(() => defaultPolicy.GetAsync(
            http,
            new Uri(UpdatePolicy.DefaultPolicyUrl),
            UpdateResourceKind.Policy,
            "test",
            CancellationToken.None));
    }

    [Fact]
    public async Task SourcePolicy_SendsConditionalHeadersAndAcceptsNotModified()
    {
        var handler = new ConditionalRequestHandler();
        using var http = new HttpClient(handler);
        var policy = new UpdateSourcePolicy("http://127.0.0.1:5899/catalog.json");
        DateTimeOffset modified = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        using HttpResponseMessage response = await policy.GetAsync(
            http,
            new Uri("http://127.0.0.1:5899/catalog.json"),
            UpdateResourceKind.Manifest,
            "test",
            CancellationToken.None,
            request =>
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", "\"catalog-v1\"");
                request.Headers.IfModifiedSince = modified;
            });

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal("\"catalog-v1\"", handler.IfNoneMatch);
        Assert.Equal(modified, handler.IfModifiedSince);
    }

    private sealed class RedirectEscapeHandler : HttpMessageHandler
    {
        private readonly Uri _destination;

        public RedirectEscapeHandler(Uri? destination = null)
        {
            _destination = destination ?? new Uri("https://evil.example.com/pkg.zip");
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                RequestMessage = request,
            };
            response.Headers.Location = _destination;
            return Task.FromResult(response);
        }
    }

    private sealed class ConditionalRequestHandler : HttpMessageHandler
    {
        public string? IfNoneMatch { get; private set; }

        public DateTimeOffset? IfModifiedSince { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            IfNoneMatch = request.Headers.TryGetValues("If-None-Match", out IEnumerable<string>? etags)
                ? etags.SingleOrDefault()
                : null;
            IfModifiedSince = request.Headers.IfModifiedSince;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)
            {
                RequestMessage = request,
            });
        }
    }

    private static NexusVersion V(string text)
    {
        Assert.True(NexusVersion.TryParse(text, out NexusVersion version));
        return version;
    }
}

/// <summary>更新域 L1/L2：当前扁平根 zip 校验、条目白名单与 SHA256 比对。</summary>
