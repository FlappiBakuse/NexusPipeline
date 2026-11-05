using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ExtensionDataTests
{
    [Fact(DisplayName = "Installed Document Cas Saves Replay The Original Commit And Preserve Later Saved Bytes")]
    public async Task ReplaysDocumentWrites()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend");
        var frontend = await FrontendInstalledTests.OpenAsync(fixture);
        try
        {
            Assert.Equal(JsonValueKind.Null, (await Invoke(frontend, "doc-read", new { name = "settings" })).ValueKind);
            var operation = Guid.NewGuid(); var input = new { name = "settings", revision = 0, value = new { title = "original" } };
            var first = await Invoke(frontend, "doc-write", input, operation);
            Assert.True(first.GetProperty("IsNew").GetBoolean()); Assert.Equal(1, first.GetProperty("Revision").GetInt64());
            _ = await Invoke(frontend, "doc-write", new { name = "settings", revision = 1, value = new { title = "later" } }, Guid.NewGuid());
            var replay = await Invoke(frontend, "doc-write", input, operation);
            Assert.False(replay.GetProperty("IsNew").GetBoolean()); Assert.Equal(1, replay.GetProperty("Revision").GetInt64());
            Assert.Equal(first.GetProperty("Sha256").GetString(), replay.GetProperty("Sha256").GetString());
            var saved = await Invoke(frontend, "doc-read", new { name = "settings" });
            Assert.Equal(2, saved.GetProperty("Revision").GetInt64());
            using var value = JsonDocument.Parse(Convert.FromBase64String(saved.GetProperty("Json").GetString()!));
            Assert.Equal("later", value.RootElement.GetProperty("title").GetString());
            var committed = await Invoke(frontend, "doc-commit", new { operationId = operation });
            Assert.Equal(1, committed.GetProperty("Revision").GetInt64());
            Assert.Equal(JsonValueKind.Null, (await Invoke(frontend, "doc-commit", new { operationId = operation }, subject: "other")).ValueKind);
            await Assert.ThrowsAsync<OperationConflictException>(() => Invoke(frontend, "doc-write",
                new { name = "settings", revision = 0, value = new { title = "different" } }, operation));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(frontend, "doc-write", input, Guid.NewGuid()));
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory(DisplayName = "Document Names Cannot Escape The Installed Extension Namespace")]
    [InlineData("../other")]
    [InlineData("UPPER")]
    public async Task RejectsForeignNames(string name)
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend");
        var frontend = await FrontendInstalledTests.OpenAsync(fixture);
        try { await Assert.ThrowsAsync<ArgumentException>(() => Invoke(frontend, "doc-read", new { name })); }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact(DisplayName = "An Installed Http Port Checks The Manifest Origin Before Invoking The Configured Transport")]
    public async Task EnforcesNetworkOrigins()
    {
        var calls = 0;
        var handler = new Handler(async (message, cancellation) =>
        {
            calls++; Assert.Equal("declared", Assert.Single(message.Headers.GetValues("X-Fixture")));
            Assert.Equal("identity", Assert.Single(message.Headers.AcceptEncoding).Value);
            Assert.Equal("{\"input\":1}", await message.Content!.ReadAsStringAsync(cancellation));
            return new(HttpStatusCode.OK) { RequestMessage = message, Content = new StringContent("{\"accepted\":true}") };
        });
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend", portDependencies:
            new(null, null, null, null, null, null, () => new HttpClient(handler, disposeHandler: false)));
        var frontend = await FrontendInstalledTests.OpenAsync(fixture);
        try
        {
            var result = await Invoke(frontend, "http-request", new { url = "https://fixture.invalid/api", method = "POST", content = "{\"input\":1}", maximum = 100 }, Guid.NewGuid());
            Assert.Equal(200, result.GetProperty("StatusCode").GetInt32()); Assert.Equal(1, calls);
            foreach (var url in new[] { "https://other.invalid/api", "https://fixture.invalid:444/api", "https://user:pass@fixture.invalid/api", "https://fixture.invalid/api#fragment" })
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Invoke(frontend, "http-request", new { url, method = "GET", maximum = 100 }, Guid.NewGuid()));
            Assert.Equal(1, calls);
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); handler.Dispose(); }
    }

    [Theory(DisplayName = "Response Limits And Redirect Authority Are Enforced Before Returning Plugin Bytes")]
    [InlineData("length")]
    [InlineData("redirect")]
    [InlineData("encoding")]
    public async Task BoundsNetworkResponses(string fault)
    {
        using var handler = new Handler((message, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = message, Content = new StringContent("too much data") };
            if (fault == "redirect") response.RequestMessage = new(HttpMethod.Get, "https://other.invalid/");
            if (fault == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(response);
        });
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend", portDependencies:
            new(null, null, null, null, null, null, () => new HttpClient(handler, disposeHandler: false)));
        var frontend = await FrontendInstalledTests.OpenAsync(fixture);
        try { await Assert.ThrowsAsync<IOException>(() => Invoke(frontend, "http-request", new { url = "https://fixture.invalid/", method = "GET", maximum = fault == "length" ? 2 : 100 }, Guid.NewGuid())); }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact(DisplayName = "Actual Installed Http Transmits Standard Field Tokens Including Upstream Underscores But Rejects Reserved Invalid And Injected Headers Before Transport")]
    public async Task TransmitsTokenHeaders()
    {
        var calls = 0;
        using var handler = new Handler((message, _) =>
        {
            calls++; Assert.Equal("synthetic", Assert.Single(message.Headers.GetValues("distinct_id")));
            Assert.Equal("token", Assert.Single(message.Headers.GetValues("X-!#$%&'*+.^_`|~")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = message, Content = new StringContent("{}") });
        });
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend", portDependencies:
            new(null, null, null, null, null, null, () => new HttpClient(handler, disposeHandler: false)));
        var frontend = await FrontendInstalledTests.OpenAsync(fixture);
        try
        {
            _ = await Invoke(frontend, "http-request", new { url = "https://fixture.invalid/", method = "GET", maximum = 100,
                headers = new Dictionary<string, string> { ["distinct_id"] = "synthetic", ["X-!#$%&'*+.^_`|~"] = "token" } }, Guid.NewGuid());
            foreach (var name in new[] { "bad name", "bad:field", "bad;field", "bad/field", "非ASCII", "Host", "Connection", "Proxy-Authorization", "Content-Type", "Transfer-Encoding", "Accept-Encoding" })
                await Assert.ThrowsAsync<ArgumentException>(() => Invoke(frontend, "http-request", new { url = "https://fixture.invalid/", method = "GET", maximum = 100,
                    headers = new Dictionary<string, string> { [name] = "synthetic" } }, Guid.NewGuid()));
            foreach (var value in new[] { "line\rbreak", "line\nbreak", "null\0byte" })
                await Assert.ThrowsAsync<ArgumentException>(() => Invoke(frontend, "http-request", new { url = "https://fixture.invalid/", method = "GET", maximum = 100,
                    headers = new Dictionary<string, string> { ["X-Fixture"] = value } }, Guid.NewGuid()));
            Assert.Equal(1, calls);
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    private static async Task<JsonElement> Invoke(InstalledFrontend frontend, string operation, object input,
        Guid? operationId = null, string subject = "local")
    {
        var result = await frontend.InvokeAsync(operation, operationId, subject, true, JsonSerializer.SerializeToUtf8Bytes(input), () => { }, default);
        using var parsed = JsonDocument.Parse(result.Content); return parsed.RootElement.Clone();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}
