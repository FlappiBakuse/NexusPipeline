using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CheckInTests
{
    [Theory(DisplayName = "The Actual CheckIn Zip Uses Host Atomic Secrets Manual Sources And Cross Task Effects With Offline Production Http")]
    [InlineData(200)]
    [InlineData(1511)]
    [InlineData(0)]
    public async Task RunsInstalledTask(int responseCode)
    {
        using var inputs = new BuiltExtensionInputs("game-checkin", "NEXUS_PACKAGED_CHECKIN_RECEIPT");
        await using var fixture = new CheckInFixture(inputs, responseCode); await fixture.OpenAsync();
        var state = await fixture.Call("read-state", new { }, read: true);
        Assert.Equal(0, state.GetProperty("configurationRevision").GetInt64()); Assert.Empty(state.GetProperty("tasks").EnumerateArray());
        var first = await Save("first", scheduled: false); var second = await Save("second", scheduled: true);
        Assert.Equal(JsonValueKind.Null, TaskView(state, first).GetProperty("nextRunAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, TaskView(state, second).GetProperty("nextRunAt").ValueKind);
        Assert.Equal(0, fixture.Http.Roles); Assert.Equal(0, fixture.Http.Submissions);
        foreach (var task in new[] { first, second })
            foreach (var field in Credentials.Keys)
            {
                var secret = await fixture.Secrets.GetStateAsync(new("game-checkin", task, field), default);
                Assert.True(secret.IsReadable); Assert.Equal(1, secret.Revision);
            }
        fixture.Http.BeforeSubmission = () => Assert.Equal(1L, Count("SELECT COUNT(*) FROM main.work_effect_events WHERE kind='submitted';"));
        _ = await fixture.Call("run-task", new { taskId = first }); var completed = await fixture.Completed(first);
        Assert.Equal(completed.Source.Document.LocalDate.ToString("MM", CultureInfo.InvariantCulture), fixture.Http.Month);
        state = await fixture.Call("read-state", new { }, read: true);
        var result = TaskView(state, first).GetProperty("recentRun").GetProperty("results")[0];
        Assert.Equal(responseCode == 0 ? "success_unconfirmed" : responseCode == 200 ? "success" : "already", result.GetProperty("code").GetString());
        Assert.Equal(responseCode == 0 ? "unknown" : "confirmed", result.GetProperty("effectState").GetString());
        _ = await fixture.Call("run-task", new { taskId = second }); _ = await fixture.Completed(second);
        Assert.Equal(1, fixture.Http.Submissions); Assert.Equal(responseCode == 0 ? 1 : 2, fixture.Http.Roles);
        state = await fixture.Call("read-state", new { }, read: true);
        var shared = TaskView(state, second).GetProperty("recentRun").GetProperty("results")[0];
        Assert.Equal(responseCode == 0 ? "effect_unknown" : "already", shared.GetProperty("code").GetString());
        Assert.Equal(1L, Count("SELECT COUNT(*) FROM main.work_effect_events WHERE kind='submitted';"));
        Assert.Equal(2L, Count("SELECT COUNT(*) FROM main.work_effect_claims;"));
        Assert.Equal(0L, Count("SELECT COUNT(*) FROM main.schedule_minute_claims;"));
        WorkEffectInventory.Verify(fixture.Catalog.Instance, fixture.Catalog.Database, new());
        new SecretDocumentInventory(fixture.Catalog.Instance, fixture.Catalog.Database, new()).Verify();
        foreach (var task in new[] { first, second })
        {
            state = await fixture.Call("delete-task", new { taskId = task, expectedRevision = state.GetProperty("configurationRevision").GetInt64() });
            foreach (var field in Credentials.Keys)
                Assert.False((await fixture.Secrets.GetStateAsync(new("game-checkin", task, field), default)).IsReadable);
        }
        Assert.Empty(state.GetProperty("tasks").EnumerateArray()); inputs.VerifyUnchanged();
        using var connection = DatabaseConnections.Open(fixture.Catalog.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var documents = connection.CreateCommand(); documents.CommandText = "SELECT content FROM extensions.extension_documents WHERE extension_id='game-checkin';";
        using var rows = documents.ExecuteReader();
        while (rows.Read()) Assert.DoesNotContain("synthetic-private-token", Encoding.UTF8.GetString((byte[])rows.GetValue(0)), StringComparison.Ordinal);

        async Task<Guid> Save(string name, bool scheduled)
        {
            var editor = await fixture.Call("open-editor", new { taskId = (Guid?)null, expectedRevision = state.GetProperty("configurationRevision").GetInt64() });
            var secrets = Credentials.ToDictionary(pair => pair.Key, pair => new { action = "set", fieldGeneration = 0, value = pair.Value });
            state = await fixture.Call("save-task", new { editorSessionId = editor.GetProperty("editorSessionId").GetString(), secrets, draft = new {
                id = (Guid?)null, name, remark = "", enabled = scheduled, games = new { kuro = new[] { "ww" } },
                schedules = scheduled ? new[] { new { id = Guid.NewGuid(), days = new[] { 0, 1, 2, 3, 4, 5, 6 }, enabled = true, time = "09:00" } } : [],
                notification = new { enabled = false, notificationTaskIds = Array.Empty<string>() } } });
            return state.GetProperty("tasks").EnumerateArray().Single(task => task.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();
        }
        long Count(string sql)
        {
            using var read = DatabaseConnections.Open(fixture.Catalog.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
            using var query = read.CreateCommand(); query.CommandText = sql; return (long)query.ExecuteScalar()!;
        }
    }

    private static JsonElement TaskView(JsonElement state, Guid id) => state.GetProperty("tasks").EnumerateArray().Single(task => task.GetProperty("id").GetGuid() == id);
    internal static readonly IReadOnlyDictionary<string, string> Credentials = new Dictionary<string, string>
    {
        ["cn"] = "cookie_token_v2=synthetic-cn-token; account_id_v2=123456",
        ["os"] = "ltoken_v2=synthetic-os-token; ltuid_v2=123456",
        ["skland"] = "synthetic-skland-token", ["skport"] = "synthetic-skport-token", ["kuro"] = "synthetic-private-token",
        ["tajiduo"] = JsonSerializer.Serialize(new { accessToken = "synthetic-access", refreshToken = "synthetic-refresh", uid = "123456",
            deviceId = "synthetic-device", roleId = "789123", sdkToken = "synthetic-sdk-token", sdkUid = "456789" })
    };
}
