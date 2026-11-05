using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class OperationQueryTests
{
    [Fact(DisplayName = "Extension History Pages Only The Current Subject And Keeps Persisted Action And Outcome")]
    public async Task PagesOwnedHistory()
    {
        await using var fixture = new PackageServiceFixture();
        var catalog = await fixture.Service.RefreshAsync("local", ExtensionStoreChannel.Stable, default);
        var id = Guid.NewGuid(); _ = await fixture.Service.ApplyAsync(id, "local", fixture.Command(catalog), default);
        await fixture.WaitCompletedAsync(id); var owner = fixture.Package.Catalog;
        IExtensionOperationQueries query = new ExtensionQueryService(owner.Instance, owner.Lease, owner.Database,
            owner.Contracts, fixture.Roots);
        var page = query.List("local", 0, 1);
        var item = Assert.Single(page.Items);
        Assert.Equal(id, item.OperationId); Assert.Equal("fixture-plugin", item.ExtensionId);
        Assert.Equal(ExtensionApplyAction.Install, item.Action); Assert.Equal("completed", item.Status);
        Assert.Equal(ExtensionApplyOutcome.Installed, item.Outcome); Assert.Null(page.NextOffset);
        Assert.Empty(query.List("another-subject", 0, 20).Items);
        Assert.Throws<ArgumentException>(() => query.List("local", 0, 51));
    }
    [Theory(DisplayName = "The Original Completed Installation Outcome Is Owned By Its Subject And Its Activation Is Historical")]
    [InlineData(false)] [InlineData(true)]
    public async Task BindsHistoricalOutcomes(bool enabled)
    {
        await using var fixture = new PackageServiceFixture(enabled);
        await fixture.Runtime.StartAsync(default);
        var catalog = await fixture.Service.RefreshAsync("local", ExtensionStoreChannel.Stable, default);
        var id = Guid.NewGuid(); _ = await fixture.Service.ApplyAsync(id, "local", fixture.Command(catalog), default);
        await fixture.WaitCompletedAsync(id); var owner = fixture.Package.Catalog;
        IExtensionOperationQueries query = new ExtensionQueryService(owner.Instance, owner.Lease, owner.Database,
            owner.Contracts, fixture.Roots);
        var result = query.Read(id, "local")!;
        Assert.Equal(ExtensionApplyOutcome.Installed, result.Outcome); Assert.Equal(JournalPhase.Committed, result.Phase);
        Assert.Equal(1, result.ResultInstalledRevision); Assert.True(result.ResultIsInstalled);
        Assert.Equal(enabled, result.HistoricalActivationId.HasValue);
        Assert.Null(query.Read(id, "different-subject")); Assert.Null(query.Read(Guid.NewGuid(), "local"));
        await fixture.Runtime.StopAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(result, query.Read(id, "local")); Assert.All(fixture.Runtime.ReadStates(), state => Assert.Null(state.ActivationId));
    }

    [Fact(DisplayName = "Accepted Sql Without The Initial Physical Journal Replays And Queries Without Creating Or Executing Anything")]
    public void ReplaysAcceptedMetadata()
    {
        using var fixture = new PayloadAcceptanceFixture(); var prepared = fixture.Prepare();
        var owner = fixture.Package.Catalog; var observer = new FailAfterAcceptance();
        var request = prepared.UseAcceptance(owner.Instance, owner.Database, fixture.Roots,
            (_, _, source) => ExtensionApplyRequest.FromSource(source));
        var acceptance = new PayloadApplyAcceptance(owner.Instance, owner.Contracts, owner.Database, fixture.Roots, observer);
        Assert.Throws<IOException>(() => acceptance.AcceptPrepared(prepared, DateTimeOffset.UtcNow));
        var directory = Path.Combine(owner.Instance.RuntimeRoot, "journals", observer.JournalId.ToString("D"));
        Assert.False(Path.Exists(directory));
        var replay = new PayloadAcceptanceLookup(owner.Instance, owner.Database, owner.Contracts, fixture.Roots)
            .ReadAccepted(fixture.Package.OperationId, "local", request)!;
        Assert.False(replay.Acceptance.IsNew); Assert.Equal(observer.JournalId, replay.JournalId);
        var query = new ExtensionQueryService(owner.Instance, owner.Lease, owner.Database, owner.Contracts, fixture.Roots);
        var state = query.Read(fixture.Package.OperationId, "local")!;
        Assert.Equal(ExtensionApplyOutcome.Pending, state.Outcome); Assert.Equal(JournalPhase.Prepared, state.Phase);
        Assert.Equal("accepted", state.Operation.Status); Assert.Null(state.ResultInstalledRevision);
        Assert.Null(query.Read(fixture.Package.OperationId, "different-subject"));
        Assert.False(Path.Exists(directory)); Assert.False(Path.Exists(fixture.Target));
        Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "unknown.txt"), "preserved");
        Assert.ThrowsAny<IOException>(() => query.Read(fixture.Package.OperationId, "local"));
        Assert.Equal("preserved", File.ReadAllText(Path.Combine(directory, "unknown.txt")));
    }

    [Theory(DisplayName = "An Actual Stopped Writer Restoration Is Reported As Original Restored And Never As A New Installation")]
    [InlineData(false)] [InlineData(true)]
    public async Task ReportsOriginalRestoration(bool upgrade)
    {
        await using var fixture = new InstallationCrashFixture("physical",
            upgrade ? PayloadAction.Upgrade : null, upgrade ? "candidate-moved" : "candidate-materialized");
        await fixture.StartAsync(); Assert.True((await fixture.StopAsync()).CompleteJobStopped);
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Package.Root, "inspection"),
            new JournalStartupAdmission(fixture.Instance, new(), fixture.Roots));
        Assert.True(new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots).Recover(lease).Current.IsReady);
        var query = new ExtensionQueryService(fixture.Instance, lease, database, new(), fixture.Roots);
        var state = query.Read(fixture.OperationId, "local:installation-owner")!;
        Assert.Equal(ExtensionApplyOutcome.OriginalRestored, state.Outcome); Assert.Equal("completed", state.Operation.Status);
        Assert.Equal(upgrade ? 1 : 0, state.ResultInstalledRevision); Assert.Equal(upgrade, state.ResultIsInstalled);
        Assert.Null(state.HistoricalActivationId); Assert.Null(query.Read(fixture.OperationId, "local"));
    }

    private sealed class FailAfterAcceptance : IPayloadPersistenceObserver
    {
        internal Guid JournalId;
        public void Reached(PayloadPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            if (point != PayloadPersistencePoint.AcceptanceCommitted) return;
            JournalId = journalId; throw new IOException("fixture_after_actual_acceptance_commit");
        }
    }
}
