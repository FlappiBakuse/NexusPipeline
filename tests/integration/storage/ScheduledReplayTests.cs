using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Notifications;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ScheduledReplayTests
{
    [Fact(DisplayName = "A Committed Not Started Result With A Failed Fresh Read Releases Its Activity Only After Exact Replay")]
    public async Task ReplaysFailedProjection()
    {
        await using var fixture = new NotificationScheduledFixture();
        await fixture.Activate();
        fixture.Clock.BeforeUtcRead = () =>
        {
            if (fixture.Base.Count("scheduled_provider_occurrences") != 1) return;
            fixture.Clock.BeforeUtcRead = null; fixture.Clock.Move(TimeSpan.FromMinutes(3));
        };
        fixture.Base.Execute("CREATE TRIGGER corrupt_after_provider_result AFTER INSERT ON scheduled_provider_result BEGIN UPDATE scheduled_provider_occurrences SET size_bytes=size_bytes+1 WHERE occurrence_id=NEW.occurrence_id; END;");
        try { await Assert.ThrowsAnyAsync<IOException>(() => fixture.Tick.TickAsync()); }
        finally { fixture.Base.Execute("DROP TRIGGER corrupt_after_provider_result;"); fixture.Clock.BeforeUtcRead = null; }
        Assert.Equal(1, fixture.Base.Count("scheduled_provider_result")); Assert.Equal(0, fixture.Base.Count("scheduled_provider_pending"));
        Assert.Equal(0, fixture.Base.Count("scheduled_provider_begin")); Assert.Equal(0, fixture.PlannedCalls); Assert.Equal(0, fixture.Effects);
        var session = Guid.NewGuid();
        Assert.Null(fixture.Base.Lifecycle.RequestClose(session, Guid.NewGuid()).Permit);
        fixture.Base.Execute("UPDATE scheduled_provider_occurrences SET size_bytes=size_bytes-1;");
        var before = Assert.Single(fixture.States()); Assert.Equal("notStarted", before.Result!.Completion);
        Assert.Null(before.Begin); Assert.Equal("scheduled_provider_deadline", before.Close!.ReasonCode);
        await fixture.Tick.TickAsync();
        var replayed = Assert.Single(fixture.States());
        Assert.Equal(before.Source.Reference, replayed.Source.Reference); Assert.Equal(before.ResultReference, replayed.ResultReference);
        Assert.Equal(before.CloseReference, replayed.CloseReference); Assert.Equal(before.Result, replayed.Result);
        Assert.Equal(0, fixture.PlannedCalls);
        Assert.NotNull(fixture.Base.Lifecycle.RequestClose(session, Guid.NewGuid()).Permit);
        DeliveryReferenceIndex.VerifyReadOnly(fixture.Base.Source.Instance, fixture.Base.Source.Database, new());
    }
}
