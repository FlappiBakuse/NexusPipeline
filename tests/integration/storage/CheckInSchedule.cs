using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CheckInSchedule
{
    [Fact(DisplayName = "An Actual Installed CheckIn Task Shows Its Newest Scheduled Runs Across Distinct Plan Identities")]
    public async Task PagesScheduledTaskRuns()
    {
        using var inputs = new BuiltExtensionInputs("game-checkin", "NEXUS_PACKAGED_CHECKIN_RECEIPT");
        var clock = new CheckInClock();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new CheckInHttp(200)
        {
            BeforeSubmission = () =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(35))) throw new IOException("fixture_schedule_release_timed_out");
            }
        };
        await using var fixture = new CheckInFixture(inputs, http, clock);
        await fixture.OpenAsync();
        var editor = await fixture.Call("open-editor", new { taskId = (Guid?)null, expectedRevision = 0 });
        var secrets = new Dictionary<string, object>();
        foreach (var field in CheckInTests.Credentials.Keys) secrets[field] = field == "kuro" ?
            new { action = "set", fieldGeneration = 0, value = CheckInTests.Credentials[field] } : new { action = "keep", fieldGeneration = 0 };
        const int acceptedRuns = 22;
        var localTime = clock.GetLocalNow().ToString("HH:mm", CultureInfo.InvariantCulture);
        var plans = Enumerable.Range(0, acceptedRuns).Select(_ => new
        {
            id = Guid.NewGuid(), days = new[] { 0, 1, 2, 3, 4, 5, 6 }, enabled = true, time = localTime
        }).ToArray();
        var state = await fixture.Call("save-task", new { editorSessionId = editor.GetProperty("editorSessionId").GetString(), secrets, draft = new
        {
            id = (Guid?)null, name = "scheduled history", remark = "", enabled = true, games = new { kuro = new[] { "ww" } },
            schedules = plans, notification = new { enabled = false, notificationTaskIds = Array.Empty<string>() }
        } });
        var task = Assert.Single(state.GetProperty("tasks").EnumerateArray()).GetProperty("id").GetGuid();
        fixture.Schedules.Start();
        JsonElement active;
        Guid activeOperation;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(25));
            await WaitCompleted(acceptedRuns - 1);
            activeOperation = Assert.Single(ReadOperations("accepted"));
            active = await fixture.Call("read-state", new { }, read: true);
        }
        finally { release.Set(); }
        await WaitCompleted(acceptedRuns);
        await fixture.Schedules.StopAsync();

        var recorded = new List<ScheduledProviderState>();
        ScheduledProviderInventory.Visit(fixture.Catalog.Instance, fixture.Catalog.Database, new(), recorded.Add);
        Assert.Equal(acceptedRuns, recorded.Count);
        Assert.All(recorded, row =>
        {
            Assert.Equal(task, row.Source.Document.ManualTask!.TaskId);
            Assert.NotEqual(task, row.Source.Document.Task.TaskId);
            Assert.NotNull(row.Result);
        });
        Assert.Equal(acceptedRuns, recorded.Select(row => row.Source.Document.Task.TaskId).Distinct().Count());
        var byOperation = recorded.ToDictionary(row => row.Source.Document.OperationId);
        var current = await fixture.Call("read-state", new { }, read: true);
        var recent = ReadOperations("completed").Take(16).ToArray();
        Assert.DoesNotContain(activeOperation, recent);
        var expected = recent.Select(id => byOperation[id].Source.Document.Invocation).ToArray();
        CheckPage(active, true);
        CheckPage(current, false);
        Assert.Equal(0, fixture.Execution.Count("manual_work_runs"));
        inputs.VerifyUnchanged();

        void CheckPage(JsonElement snapshot, bool running)
        {
            var fact = Assert.Single(snapshot.GetProperty("tasks").EnumerateArray());
            Assert.Equal(running, fact.GetProperty("isRunning").GetBoolean());
            var visible = fact.GetProperty("runs").EnumerateArray().ToArray();
            Assert.Equal(expected.Select(value => value.RunId), visible.Select(value => value.GetProperty("id").GetGuid()));
            Assert.Equal(expected.Select(value => value.AttemptId), visible.Select(value => value.GetProperty("attemptId").GetGuid()));
            Assert.All(visible, value => Assert.Equal("scheduled", value.GetProperty("trigger").GetString()));
        }

        async Task WaitCompleted(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            while (ReadOperations("completed").Count != count)
                await Task.Delay(20, timeout.Token);
        }

        List<Guid> ReadOperations(string status)
        {
            using var connection = DatabaseConnections.Open(fixture.Catalog.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
            using var query = DatabaseConnections.Command(connection, null,
                "SELECT operation_id FROM main.operations WHERE operation_kind='schedule.provider-invoke' AND status=$0 ORDER BY rowid DESC;", status);
            var result = new List<Guid>();
            using var rows = query.ExecuteReader();
            while (rows.Read()) result.Add(Guid.ParseExact(rows.GetString(0), "D"));
            return result;
        }
    }

    [Theory(DisplayName = "An Actual Installed CheckIn Schedule Uses The Current Host Minute And Sends Only Its Explicit Saved Notification Selection Through The Real Queue")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchesSavedSelection(bool notify)
    {
        using var inputs = new BuiltExtensionInputs("game-checkin", "NEXUS_PACKAGED_CHECKIN_RECEIPT");
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        var clock = new CheckInClock();
        await using var fixture = new CheckInFixture(inputs, new CheckInHttp(200), clock);
        var selected = await fixture.SaveNotification(sink.Address); _ = await fixture.SaveNotification(sink.Address);
        await fixture.OpenAsync(); var editor = await fixture.Call("open-editor", new { taskId = (Guid?)null, expectedRevision = 0 });
        var secret = new Dictionary<string, object>();
        foreach (var field in CheckInTests.Credentials.Keys) secret[field] = field == "kuro" ?
            new { action = "set", fieldGeneration = 0, value = CheckInTests.Credentials[field] } : new { action = "keep", fieldGeneration = 0 };
        var state = await fixture.Call("save-task", new { editorSessionId = editor.GetProperty("editorSessionId").GetString(), secrets = secret, draft = new {
            id = (Guid?)null, name = "scheduled", remark = "", enabled = true, games = new { kuro = new[] { "ww" } },
            schedules = new[] { new { id = Guid.NewGuid(), days = new[] { 0, 1, 2, 3, 4, 5, 6 }, enabled = true,
                time = clock.GetLocalNow().ToString("HH:mm", CultureInfo.InvariantCulture) } },
            notification = new { enabled = notify, notificationTaskIds = notify ? new[] { selected.ToString("D") } : Array.Empty<string>() } } });
        var task = Assert.Single(state.GetProperty("tasks").EnumerateArray()).GetProperty("id").GetGuid();
        fixture.Schedules.Start(); var completed = await fixture.CompletedSchedule(task); await fixture.Schedules.StopAsync();
        Assert.Equal("succeeded", completed.Result!.Returned!.Business.ToString().ToLowerInvariant());
        Assert.Equal(1, fixture.Http.Submissions); Assert.Empty(sink.Requests);
        Assert.Equal(notify ? 1 : 0, fixture.Execution.Count("notification_deliveries"));
        Assert.Equal(0, fixture.Execution.Count("manual_work_runs"));
        await fixture.Notifications.RunPassAsync();
        Assert.Equal(notify ? 1 : 0, sink.Requests.Count); Assert.Null(fixture.Notifications.FailureCode);
        var current = await fixture.Call("read-state", new { }, read: true);
        var run = Assert.Single(current.GetProperty("tasks").EnumerateArray()).GetProperty("recentRun");
        Assert.Equal("scheduled", run.GetProperty("trigger").GetString());
        Assert.Equal("success", run.GetProperty("status").GetString());
        Assert.Equal("success", Assert.Single(run.GetProperty("results").EnumerateArray()).GetProperty("code").GetString());
        ScheduledProviderInventory.Visit(fixture.Catalog.Instance, fixture.Catalog.Database, new(), _ => { });
        DeliveryReferenceIndex.VerifyReadOnly(fixture.Catalog.Instance, fixture.Catalog.Database, new()); inputs.VerifyUnchanged();
    }

    private sealed class CheckInClock : TimeProvider
    {
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
