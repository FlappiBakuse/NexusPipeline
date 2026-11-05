using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AdmissionCapacityTests
{
    [Fact(DisplayName = "A Full Actual Inbox Rejects A New Independent Acceptance But Keeps Replay And Releases Capacity At Ownership Transfer")]
    public void BoundsAcceptedInbox()
    {
        using var fixture = new ExecutionAdmissionFixture(inboxCapacity: 1);
        var source = fixture.Source; var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid();
        var firstRequest = source.Request(new BindingTarget(source.BindingB));
        var secondRequest = source.Request(new BindingTarget(source.OtherBinding));
        var first = fixture.Service.Start(firstId, "inbox-capacity", firstRequest, PlanBuilderFixture.CreatedAt);
        var before = fixture.Counts(); var configuration = File.ReadAllBytes(source.Configuration); var log = File.ReadAllBytes(source.Log);
        var failure = Assert.Throws<ExecutionSelectionException>(() => fixture.Service.Start(secondId, "inbox-capacity",
            secondRequest, PlanBuilderFixture.CreatedAt));
        Assert.Equal("execution_admission_inbox_full", failure.Message);
        Assert.Equal(before, fixture.Counts()); Assert.Equal(1, fixture.Inbox.PendingCount);
        Assert.True(fixture.Index.Current.IsReady);
        Assert.Equal(configuration, File.ReadAllBytes(source.Configuration)); Assert.Equal(log, File.ReadAllBytes(source.Log));
        var replay = fixture.Service.Start(firstId, "inbox-capacity", firstRequest, PlanBuilderFixture.CreatedAt);
        Assert.False(replay.Acceptance.IsNew); Assert.Equal(first.JournalId, replay.JournalId);
        using var transferred = fixture.Take(firstId);
        Assert.Equal(0, fixture.Inbox.PendingCount);
        Assert.True(fixture.Service.Start(secondId, "inbox-capacity", secondRequest, PlanBuilderFixture.CreatedAt).Acceptance.IsNew);
        Assert.Equal(1, fixture.Inbox.PendingCount);
    }

    [Fact(DisplayName = "Pending Capacity Includes Every Unpublished Reservation And Disposal Releases Only Its Own Slot Once")]
    public void CountsPendingReservations()
    {
        var inbox = new ExecutionAdmissionInbox(); var slots = new List<IDisposable>();
        try
        {
            for (var index = 0; index < 64; index++) slots.Add(inbox.Reserve(Guid.NewGuid()));
            Assert.Equal("execution_admission_inbox_full", Assert.Throws<ExecutionSelectionException>(() => inbox.Reserve(Guid.NewGuid())).Message);
            slots[0].Dispose(); slots[0].Dispose();
            using var replacement = inbox.Reserve(Guid.NewGuid());
            Assert.Throws<ExecutionSelectionException>(() => inbox.Reserve(Guid.NewGuid()));
            Assert.Equal(0, inbox.PendingCount);
        }
        finally { foreach (var slot in slots) slot.Dispose(); }
        using var available = inbox.Reserve(Guid.NewGuid());
    }
}
