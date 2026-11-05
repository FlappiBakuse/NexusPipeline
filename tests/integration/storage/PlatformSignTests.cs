using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PlatformSignTests
{
    [Theory(DisplayName = "The Original CheckIn Package Executes All Fourteen Regional Games Through Host Effects And Atomic Credential Rotation Without Replaying Lost Submissions")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunsAllPlatforms(bool lostResponse)
    {
        using var inputs = new BuiltExtensionInputs("game-checkin", "NEXUS_PACKAGED_CHECKIN_RECEIPT");
        using var http = new PlatformHttp(lostResponse);
        await using var fixture = new CheckInFixture(inputs, http); await fixture.OpenAsync();
        var state = await fixture.Call("read-state", new { }, read: true);
        var first = await Save("first"); var second = await Save("second");
        var running = first;
        http.BeforeSubmission = () => Assert.True(Count("SELECT COUNT(*) FROM work_effect_events WHERE kind='submitted';") > http.Submissions);
        http.AfterRotation = () => Assert.Equal(running == first || !lostResponse ? 2 : 1,
            fixture.Secrets.GetStateAsync(new("game-checkin", running, "tajiduo"), default).AsTask().GetAwaiter().GetResult().Revision);
        _ = await fixture.Call("run-task", new { taskId = first }); _ = await fixture.Completed(first, TimeSpan.FromMinutes(2));
        state = await fixture.Call("read-state", new { }, read: true);
        var results = TaskView(state, first).GetProperty("recentRun").GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(14, results.Length);
        Assert.All(results, result => Assert.Equal(lostResponse ? "success_unconfirmed" : "success", result.GetProperty("code").GetString()));
        Assert.Equal(14, http.Submissions); Assert.Equal(1, http.Rotations);
        Assert.Equal(14, Count("SELECT COUNT(*) FROM work_effect_events WHERE kind='submitted';"));
        running = second;
        _ = await fixture.Call("run-task", new { taskId = second }); _ = await fixture.Completed(second, TimeSpan.FromMinutes(2));
        state = await fixture.Call("read-state", new { }, read: true);
        results = TaskView(state, second).GetProperty("recentRun").GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(14, results.Length);
        Assert.All(results, result => Assert.Contains(result.GetProperty("code").GetString(), lostResponse ? new[] { "already", "effect_unknown" } : ["already"]));
        Assert.Equal(14, http.Submissions); Assert.Equal(lostResponse ? 1 : 2, http.Rotations);
        Assert.Contains("fp-it.portal101.cn", http.Origins); Assert.Contains("zonai.skland.com", http.Origins); Assert.Contains("zonai.skport.com", http.Origins);
        WorkEffectInventory.Verify(fixture.Catalog.Instance, fixture.Catalog.Database, new());
        new SecretDocumentInventory(fixture.Catalog.Instance, fixture.Catalog.Database, new()).Verify(); inputs.VerifyUnchanged();

        async Task<Guid> Save(string name)
        {
            var editor = await fixture.Call("open-editor", new { taskId = (Guid?)null, expectedRevision = state.GetProperty("configurationRevision").GetInt64() });
            var secrets = CheckInTests.Credentials.ToDictionary(pair => pair.Key, pair => new { action = "set", fieldGeneration = 0, value = pair.Value });
            state = await fixture.Call("save-task", new { editorSessionId = editor.GetProperty("editorSessionId").GetString(), secrets, draft = new {
                id = (Guid?)null, name, remark = "", enabled = false, games = new Dictionary<string, string[]> {
                    ["cn"] = ["gi", "hsr", "zzz", "bh3"], ["os"] = ["gi", "hsr", "zzz", "bh3"], ["skland"] = ["ak", "endfield"],
                    ["skport"] = ["endfield"], ["kuro"] = ["ww", "pgr"], ["tajiduo"] = ["nte"] },
                schedules = Array.Empty<object>(), notification = new { enabled = false, notificationTaskIds = Array.Empty<string>() } } });
            return state.GetProperty("tasks").EnumerateArray().Single(task => task.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();
        }
        long Count(string sql)
        {
            using var connection = DatabaseConnections.Open(fixture.Catalog.Database.DatabasePath, readOnly: true);
            using var query = connection.CreateCommand(); query.CommandText = sql; return (long)query.ExecuteScalar()!;
        }
    }
    private static JsonElement TaskView(JsonElement state, Guid id) => state.GetProperty("tasks").EnumerateArray().Single(task => task.GetProperty("id").GetGuid() == id);
}
