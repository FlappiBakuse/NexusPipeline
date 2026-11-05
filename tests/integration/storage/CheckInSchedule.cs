using System.Globalization;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Scheduling;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CheckInSchedule
{
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
