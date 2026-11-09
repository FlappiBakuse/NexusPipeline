using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ApplyWorkflowTests
{
    [Theory(DisplayName = "The Trusted Workflow Downloads Prepares Accepts And Publishes The Actual Installation")]
    [InlineData(false)] [InlineData(true)]
    public async Task PublishesTrustedInstall(bool enabled)
    {
        await using var fixture = new ExtensionWorkflowFixture(enabled);
        var flight = fixture.Coordinator.Start(Guid.NewGuid(), "local", fixture.Request);
        var accepted = await flight.WaitAcceptedAsync(default).WaitAsync(TimeSpan.FromSeconds(20));
        var result = await flight.Completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(accepted.Acceptance.IsNew); Assert.False(result.WasReplay); Assert.NotNull(result.Result);
        Assert.Equal(accepted.JournalId, result.Result.JournalId); Assert.Equal(1, result.Result.InstalledRevision);
        Assert.True(result.Result.IsInstalled); Assert.Equal(enabled, result.Result.IsEnabled);
        Assert.Equal(enabled, fixture.Activator.ReadPublishedScopes().Count == 1);
        Assert.Equal(JournalPhase.Committed, fixture.Binding().Read(accepted.JournalId, accepted.Plan).Registered.Phase);
        Assert.True(new RecoveryJournalRegistry(fixture.Package.Catalog.Database).ReadSnapshot().IsReady);
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.Package.Catalog.Instance.RuntimeRoot, "staging")));
    }

    [Fact(DisplayName = "Durable Replay Uses The Original Request Before Downloading Or Choosing The New Catalog Head")]
    public async Task PreservesDurableReplay()
    {
        await using var fixture = new ExtensionWorkflowFixture(); var id = Guid.NewGuid();
        var first = fixture.Coordinator.Start(id, "local", fixture.Request);
        var complete = await first.Completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        fixture.Package.Publish(2);
        var restarted = new ExtensionApplyCoordinator(fixture.Workflow);
        try
        {
            var replay = restarted.Start(id, "local", fixture.Request);
            var accepted = await replay.WaitAcceptedAsync(default);
            var result = await replay.Completion.Task;
            Assert.True(result.WasReplay); Assert.Null(result.Result);
            Assert.Equal(complete.Acceptance.JournalId, accepted.JournalId); Assert.False(accepted.Acceptance.IsNew);
            Assert.Throws<OperationConflictException>(() => restarted.Start(id, "another-subject", fixture.Request));
            Assert.Throws<OperationConflictException>(() => restarted.Start(id, "local",
                fixture.Request with { ExpectedSettingsRevision = fixture.Request.ExpectedSettingsRevision + 1 }));
            Assert.Equal(1, fixture.Handler.Requests);
        }
        finally { await restarted.StopAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact(DisplayName = "Client Cancellation Detaches The Waiter While The Same Host Task Completes Only One Download")]
    public async Task SharesDetachedDownloads()
    {
        await using var fixture = new ExtensionWorkflowFixture(blockTransfer: true); var id = Guid.NewGuid();
        var first = fixture.Coordinator.Start(id, "local", fixture.Request);
        await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var disconnected = new CancellationTokenSource(); disconnected.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAcceptedAsync(disconnected.Token));
        Assert.False(first.Acceptance.Task.IsCompleted);
        var second = fixture.Coordinator.Start(id, "local", fixture.Request);
        Assert.Throws<OperationConflictException>(() => fixture.Coordinator.Start(id, "another-subject", fixture.Request));
        fixture.Handler.Release.SetResult();
        var complete = await first.Completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var replay = await second.Completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(complete.Acceptance.JournalId, replay.Acceptance.JournalId);
        Assert.NotNull(complete.Result); Assert.True(complete.Result.IsInstalled); Assert.Equal(1, fixture.Handler.Requests);
    }

    [Fact(DisplayName = "An Actual Reference Conflict Cannot Be Accepted Until The Original Resource Is Released")]
    public async Task WaitsForReferences()
    {
        await using var fixture = new ExtensionWorkflowFixture();
        using var held = fixture.Package.Catalog.Resources.TryAcquire(Guid.NewGuid(),
            [new("extension:fixture-plugin", ResourceClaimMode.Reference)]).Lease!;
        Assert.NotNull(held);
        var flight = fixture.Coordinator.Start(Guid.NewGuid(), "local", fixture.Request);
        await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(flight.Acceptance.Task.IsCompleted);
        held.Dispose();
        var complete = await flight.Completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotNull(complete.Result); Assert.Equal(1, complete.Result.InstalledRevision);
    }

    [Theory(DisplayName = "Failed Hash Or Host Cancellation Before Acceptance Cannot Leave An Accepted Operation Or Owned Download")]
    [InlineData(false)] [InlineData(true)]
    public async Task RejectsUnacceptedDownloads(bool cancel)
    {
        await using var fixture = new ExtensionWorkflowFixture(blockTransfer: cancel, corrupt: !cancel);
        var before = fixture.Package.Catalog.Count("operations"); var id = Guid.NewGuid();
        var flight = fixture.Coordinator.Start(id, "local", fixture.Request);
        if (cancel)
        {
            await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Coordinator.StopAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flight.Completion.Task);
        }
        else await Assert.ThrowsAsync<IOException>(() => flight.Completion.Task);
        await Assert.ThrowsAnyAsync<Exception>(() => flight.WaitAcceptedAsync(default));
        Assert.Equal(before, fixture.Package.Catalog.Count("operations"));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.Package.Catalog.Instance.RuntimeRoot, "staging")));
        Assert.Null(fixture.Workflow.ReadAccepted(id, "local", fixture.Request));
    }
}
