using System.Runtime.Versioning;
using System.Diagnostics;
using System.Text.Json;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ActivitiesInstalledTests
{
    [Theory(DisplayName = "The Actual Installed Activities Dll Refreshes All Eleven Sources After The Editor Callback Ends And Persists Scope Owned Cache")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshesInstalledSources(bool live)
    {
        using var inputs = new BuiltExtensionInputs("game-activities", "NEXUS_PACKAGED_ACTIVITIES_RECEIPT");
        using var responses = new ActivityHttpSources(live);
        using var execution = new ExecutionAdmissionFixture();
        using var installed = new InstallRuntimeFixture(catalog: execution.CreateCatalog(),
            portDependencies: new(null, null, null, null, null, null, () => new HttpClient(responses, disposeHandler: false)),
            originalManifest: inputs.Manifest, originalManifestBytes: inputs.ManifestBytes, packageArchive: inputs.Archive);
        try
        {
            var activation = await installed.Session().ApplyAsync(installed.Prepared, default);
            Assert.True(activation.IsEnabled);
            installed.Prepared.Dispose(); installed.Verified.Dispose(); installed.Root.Dispose();
            var source = execution.Source;
            ExtensionProjectionPublication.PublishIfDirty(source.Instance, source.Database, source.Lease, new(),
                installed.Roots, execution.Resources, execution.Index);
            var frontend = Assert.IsType<InstalledFrontend>(installed.Activator.RequirePublishedRuntime("game-activities").Frontend);
            var state = await Invoke(frontend, "read-state", new { });
            var games = state.GetProperty("gameOrder").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (ba, nte) in new[] { ("cn", "cn"), ("jp", "global"), ("global", "global") })
            {
                var revoked = false;
                _ = await Invoke(frontend, "save-settings", new
                {
                    expectedRevision = state.GetProperty("settingsRevision").GetInt64(),
                    settings = new { formatVersion = 1, selectedGames = games, gameOrder = games,
                        progressions = new Dictionary<string, string> { ["blue-archive"] = ba, ["neverness-to-everness"] = nte },
                        rotationSeconds = 30, pauseOnInteraction = true }
                }, Guid.NewGuid(), () => { if (revoked) throw new UnauthorizedAccessException("original_editor_revoked"); });
                revoked = true;
                state = await AwaitFresh(frontend);
                foreach (var id in state.GetProperty("selectedFeedIds").EnumerateArray().Select(value => value.GetString()!)) expected.Add(id);
            }
            Assert.Equal(11, expected.Count); Assert.Equal(11, responses.Calls.Count);
            foreach (var id in expected)
            {
                var detail = await Invoke(frontend, "read-feed", new { feedId = id });
                var feed = detail.GetProperty("feed");
                Assert.Equal("fresh", feed.GetProperty("cacheState").GetString());
                Assert.NotEqual(JsonValueKind.Null, feed.GetProperty("lastGoodAt").ValueKind);
            }
            using var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT o.subject_id FROM extension_documents d JOIN operations o ON o.operation_id=d.operation_id " +
                "WHERE d.extension_id='game-activities' AND d.name LIKE 'feed-%';";
            using var rows = command.ExecuteReader(); var count = 0;
            while (rows.Read()) { Assert.Equal("extension:game-activities", rows.GetString(0)); count++; }
            Assert.Equal(11, count); responses.VerifyUnchanged(); inputs.VerifyUnchanged();
        }
        finally { await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact(DisplayName = "The Original Activities Package Initializes Only After Its Actual Installation Writer Releases The Published Scope")]
    public async Task ReadsOriginalPackage()
    {
        using var inputs = new BuiltExtensionInputs("game-activities", "NEXUS_PACKAGED_ACTIVITIES_RECEIPT");
        using var execution = new ExecutionAdmissionFixture();
        using var installed = new InstallRuntimeFixture(catalog: execution.CreateCatalog(),
            portDependencies: new(null, null, null, null, null, null, () => new HttpClient()),
            originalManifest: inputs.Manifest, originalManifestBytes: inputs.ManifestBytes, packageArchive: inputs.Archive);
        try
        {
            var activation = await installed.Session().ApplyAsync(installed.Prepared, default);
            Assert.True(activation.IsInstalled); Assert.True(activation.IsEnabled);
            installed.Prepared.Dispose(); installed.Verified.Dispose(); installed.Root.Dispose();
            var source = execution.Source;
            ExtensionProjectionPublication.PublishIfDirty(source.Instance, source.Database, source.Lease, new(),
                installed.Roots, execution.Resources, execution.Index);
            var frontend = Assert.IsType<InstalledFrontend>(installed.Activator.RequirePublishedRuntime("game-activities").Frontend);
            var result = await frontend.InvokeAsync("read-state", null, "activities:reader", false, "{}"u8.ToArray(), () => { }, default);
            using var state = JsonDocument.Parse(result.Content);
            Assert.Empty(state.RootElement.GetProperty("selectedFeedIds").EnumerateArray());
            Assert.Equal(8, state.RootElement.GetProperty("gameOrder").GetArrayLength());
            inputs.VerifyUnchanged();
        }
        finally { await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    private static async Task<JsonElement> AwaitFresh(InstalledFrontend frontend)
    {
        var timer = Stopwatch.StartNew(); JsonElement state = default;
        while (timer.Elapsed < TimeSpan.FromSeconds(45))
        {
            state = await Invoke(frontend, "read-state", new { });
            var selected = state.GetProperty("selectedFeedIds").EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
            if (state.GetProperty("feeds").EnumerateArray().Where(feed => selected.Contains(feed.GetProperty("id").GetString()!))
                .All(feed => feed.GetProperty("cacheState").GetString() == "fresh")) return state;
            if (!state.GetProperty("refreshing").GetBoolean() && state.GetProperty("refreshStatus").GetString() is "failed" or "partial")
                throw new IOException("installed_activity_refresh_failed:" + state.GetRawText());
            await Task.Delay(100);
        }
        throw new IOException("installed_activity_refresh_timeout:" + state.GetRawText());
    }

    private static async Task<JsonElement> Invoke(InstalledFrontend frontend, string operation, object input,
        Guid? id = null, Action? requireCaller = null)
    {
        var result = await frontend.InvokeAsync(operation, id, "activities:editor", false,
            JsonSerializer.SerializeToUtf8Bytes(input), requireCaller ?? (() => { }), default);
        using var json = JsonDocument.Parse(result.Content);
        return json.RootElement.Clone();
    }
}
