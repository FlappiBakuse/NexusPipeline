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
        var observer = new BeforeFirstRemoval(() =>
        {
            foreach (var path in sources.Append(remaining))
            {
                var original = File.ReadAllBytes(path);
                Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(path, [9, 7, 3]));
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
            File.WriteAllBytes(path, bytes[path]);
            Assert.Equal(bytes[path], File.ReadAllBytes(path));
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
        using var root = owner.Resources.TryAcquire(plan.OperationId, FileCleanupItems.Claims(plan)).Lease!;
        Assert.NotNull(root);
        using var prepared = await PreparedWorkCleanup.PrepareAsync(owner.Instance, owner.Database, owner.Lease,
            owner.Contracts, fixture.Original.Roots, owner.Resources, fixture.Original.Activator, root, plan, "local", true, default);
        _ = new FileCleanupAcceptance(owner.Instance, owner.Contracts, owner.Database, payloadRoots: fixture.Original.Roots)
            .AcceptPrepared(prepared.Identity, prepared, DateTimeOffset.UtcNow);
        using var protection = prepared.Execution.ProtectEvidence(plan);
        Assert.NotNull(protection);
        if (change == "unknown-sibling")
            File.WriteAllBytes(Path.Combine(owner.Instance.RuntimeRoot, "journals", fixture.Producer.Plan.JournalId.ToString("D"), "unknown.bin"), [8, 6, 4]);
        else
        {
            using var connection = DatabaseConnections.Open(owner.Database.DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE operations SET status='accepted' WHERE operation_id=$operation;";
            command.Parameters.AddWithValue("$operation", fixture.Producer.Plan.OperationId.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        Assert.ThrowsAny<IOException>(() => prepared.Execution.Require(plan));
        Assert.True(Directory.Exists(fixture.Workspace));
        Assert.All(new FileEffectOutbox(owner.Database).Read(plan.JournalId), item => Assert.Equal(FileEffectState.Pending, item.State));
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
