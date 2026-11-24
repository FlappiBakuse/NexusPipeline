using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class WorkScopeTests
{
    [Fact(DisplayName = "Actual Cleanup Holds Producer Bytes And Remaining File Bytes Until Their Owned Effects Finish")]
    public async Task HoldsProducerEvidence()
    {
        await using var fixture = await WorkCleanupFixture.CreateAsync();
        var owner = fixture.Original.Package.Catalog;
        var first = await WorkAdmissionTests.CommitPage(fixture, null, 1);
        var artifact = Path.Combine(owner.Instance.RuntimeRoot, fixture.Producer.CommitIntent!.Artifact.RelativePath);
        var producer = Path.Combine(owner.Instance.RuntimeRoot, "journals", fixture.Producer.Plan.JournalId.ToString("D"), "0000000001.json");
        var previous = Path.Combine(owner.Instance.RuntimeRoot, "journals", first.JournalId.ToString("D"), "0000000001.json");
        var remaining = Directory.EnumerateFiles(Path.Combine(fixture.Workspace, "before"), "*", SearchOption.AllDirectories).First();
        var sources = new[] { artifact, producer, previous };
        var bytes = sources.ToDictionary(path => path, File.ReadAllBytes);
        var identities = sources.ToDictionary(path => path, PhysicalObjectIdentity.Read);
        var observer = new BeforeFirstRemoval(() =>
        {
            foreach (var path in sources.Append(remaining))
            {
                var original = File.ReadAllBytes(path);
                var locked = Assert.Throws<IOException>(() => File.WriteAllBytes(path, [9, 7, 3]));
                Assert.Equal(32, locked.HResult & 0xffff);
                Assert.Equal(original, File.ReadAllBytes(path));
            }
        });
        var result = await fixture.Service().RunPageAsync(Guid.NewGuid(), "local", fixture.Producer.Plan.JournalId,
            fixture.Producer.PlanReference, first.JournalId, first.Plan, default, observer);
        Assert.True(observer.Fired);
        Assert.Equal(JournalPhase.Committed, new RecoveryJournalRegistry(owner.Database).ReadJournal(result.JournalId).Phase);
        Assert.False(Directory.Exists(fixture.Workspace));
        foreach (var path in sources)
        {
            Assert.Equal(bytes[path], File.ReadAllBytes(path));
            using var released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(identities[path], PhysicalObjectIdentity.Read(released.SafeFileHandle));
        }
    }

    [Theory(DisplayName = "Protected Evidence Still Rejects Fresh Unknown Journal Entries And Changed Operation Rows")]
    [InlineData("unknown-sibling")]
    [InlineData("operation-sql")]
    public async Task RechecksProtectedEvidence(string change)
    {
        await using var fixture = await WorkCleanupFixture.CreateAsync();
        var owner = fixture.Original.Package.Catalog;
        var plan = fixture.DirectPage(Guid.NewGuid());
        var steps = plan.ExtensionWork!.Page.Indices.Select((_, index) => $"remove-{index:000}").ToArray();
        Assert.NotEmpty(steps);
        using var root = owner.Resources.TryAcquire(plan.OperationId, FileCleanupItems.Claims(plan)).Lease!;
        Assert.NotNull(root);
        using var prepared = await PreparedWorkCleanup.PrepareAsync(owner.Instance, owner.Database, owner.Lease,
            owner.Contracts, fixture.Original.Roots, owner.Resources, fixture.Original.Activator, root, plan, "local", true, default);
        var accepted = new FileCleanupAcceptance(owner.Instance, owner.Contracts, owner.Database, payloadRoots: fixture.Original.Roots)
            .AcceptPrepared(prepared.Identity, prepared, DateTimeOffset.UtcNow);
        using var protection = prepared.Execution.ProtectEvidence(plan);
        Assert.NotNull(protection);
        var archive = new FileTreeScanner(SnapshotBudget.Default);
        var before = archive.Capture(fixture.Workspace, default);
        var unknown = Path.Combine(owner.Instance.RuntimeRoot, "journals", fixture.Producer.Plan.JournalId.ToString("D"), "unknown.bin");
        byte[] unknownBytes = [8, 6, 4];
        if (change == "unknown-sibling")
            File.WriteAllBytes(unknown, unknownBytes);
        else
        {
            using var connection = DatabaseConnections.Open(owner.Database.DatabasePath, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE main.operations SET status='accepted' WHERE operation_id=$operation;";
            command.Parameters.AddWithValue("$operation", fixture.Producer.Plan.OperationId.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        Assert.ThrowsAny<IOException>(() => prepared.Execution.Require(plan));
        var after = archive.Capture(fixture.Workspace, default);
        Assert.Equal(before.Shape, after.Shape);
        Assert.Equal(before.Entries, after.Entries);
        if (change == "unknown-sibling") Assert.Equal(unknownBytes, File.ReadAllBytes(unknown));
        else
        {
            using var connection = DatabaseConnections.Open(owner.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT status FROM main.operations WHERE operation_id=$operation;";
            command.Parameters.AddWithValue("$operation", fixture.Producer.Plan.OperationId.ToString("D"));
            Assert.Equal("accepted", command.ExecuteScalar());
        }
        var effects = new FileEffectOutbox(owner.Database).Read(plan.JournalId);
        Assert.Equal(steps, effects.Select(item => item.StepId).Order(StringComparer.Ordinal));
        Assert.All(effects, item => Assert.Equal(
            new FileEffect(plan.JournalId, item.StepId, FileEffectAction.RemoveOwned, FileEffectState.Pending, accepted.Plan.Sha256, null), item));
    }

    private sealed class BeforeFirstRemoval(Action action) : ICleanupPersistenceObserver
    {
        internal bool Fired;
        public void Reached(CleanupPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            if (Fired || point != CleanupPersistencePoint.JournalFlushed || phase != JournalPhase.Applying || revision != 2) return;
            Fired = true; action();
        }
    }
}
