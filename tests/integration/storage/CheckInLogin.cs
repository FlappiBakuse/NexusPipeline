using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Desktop;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Secrets;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CheckInLogin
{
    [Theory(DisplayName = "The Original CheckIn Package Accepts An Official Login Fixture Only Through Its Current Native Capture Then Atomically Saves Its Opaque Candidate Without Secret Readback")]
    [InlineData("cn")]
    [InlineData("os")]
    [InlineData("skland")]
    [InlineData("skport")]
    [InlineData("kuro")]
    public async Task SavesNativeCandidate(string platform)
    {
        using var inputs = new BuiltExtensionInputs("game-checkin", "NEXUS_PACKAGED_CHECKIN_RECEIPT");
        using var http = new PlatformHttp(false);
        await using var fixture = new CheckInFixture(inputs, http, native: true); await fixture.OpenAsync();
        var editor = await fixture.Call("open-editor", new { taskId = (Guid?)null, expectedRevision = 0 });
        var editorId = editor.GetProperty("editorSessionId").GetString();
        var payload = new { editorSessionId = editorId, platformId = platform, fieldGeneration = 0 };
        using var outsider = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => fixture.Frontend.InvokeAsync("begin-browser-login", Guid.NewGuid(), "fixture:unbound-client", false,
            JsonSerializer.SerializeToUtf8Bytes(payload), () => { }, outsider.Token, clientId: Guid.NewGuid(), clientLifetime: outsider.Token).AsTask());
        fixture.Logins!.Poll(fixture.Native!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = fixture.Call("begin-browser-login", payload, cancellation: deadline.Token);
        BrowserLoginOffer? offer = null;
        while (offer is null) { offer = fixture.Logins.Poll(fixture.Native!).SingleOrDefault(); if (offer is null) await Task.Delay(20, deadline.Token); }
        Assert.Equal("game-checkin", offer.ExtensionId); Assert.Equal("checkin-" + platform + "-v1", offer.Policy.Id);
        Assert.False(pending.IsCompleted);
        var values = Capture(platform).ToDictionary(pair => pair.Key, pair => new SecretValue(pair.Value));
        try { fixture.Logins.Complete(fixture.Native!, offer.CaptureId, offer.Nonce, "completed", values); }
        finally { foreach (var value in values.Values) value.Dispose(); }
        var candidate = await pending; Assert.DoesNotContain("synthetic-", candidate.ToString(), StringComparison.Ordinal);
        editor = await fixture.Call("read-editor", new { editorSessionId = editorId }, read: true);
        Assert.DoesNotContain("synthetic-", editor.ToString(), StringComparison.Ordinal); Assert.Empty(fixture.Logins.Poll(fixture.Native!));
        var secrets = new Dictionary<string, object>();
        foreach (var field in CheckInTests.Credentials.Keys) secrets[field] = field == platform ? new { action = "set",
            fieldGeneration = candidate.GetProperty("fieldGeneration").GetInt64(), candidateId = candidate.GetProperty("candidateId").GetString() } :
            new { action = "keep", fieldGeneration = 0 };
        var game = platform switch { "cn" or "os" => "gi", "skland" => "ak", "skport" => "endfield", _ => "ww" };
        var state = await fixture.Call("save-task", new { editorSessionId = editorId, secrets, draft = new { id = (Guid?)null,
            name = platform + " native", remark = "", enabled = false, games = new Dictionary<string, string[]> { [platform] = [game] },
            schedules = Array.Empty<object>(), notification = new { enabled = false, notificationTaskIds = Array.Empty<string>() } } });
        var task = Assert.Single(state.GetProperty("tasks").EnumerateArray()).GetProperty("id").GetGuid();
        var stored = await fixture.Secrets.GetStateAsync(new("game-checkin", task, platform), default);
        Assert.True(stored.IsReadable); Assert.Equal(1, stored.Revision); Assert.Equal(0, http.Submissions);
        Assert.DoesNotContain("synthetic-", state.ToString(), StringComparison.Ordinal);
        new SecretDocumentInventory(fixture.Catalog.Instance, fixture.Catalog.Database, new()).Verify(); inputs.VerifyUnchanged();
    }

    private static Dictionary<string, string> Capture(string platform) => platform switch
    {
        "cn" => new() { ["d0-cookie_token_v2"] = "synthetic-cn-token", ["d0-account_id_v2"] = "123456" },
        "os" => new() { ["d0-ltoken_v2"] = "synthetic-os-token", ["d0-ltuid_v2"] = "123456" },
        "skland" => new() { ["passport-token"] = "synthetic-skland-token", ["device"] = "{\"id\":\"Bsynthetic-device\"}" },
        "skport" => new() { ["passport-token"] = "synthetic-skport-token" },
        "kuro" => new() { ["auth-token"] = "synthetic-private-token", ["distinct-id"] = "synthetic-device" },
        _ => throw new InvalidOperationException()
    };
}
