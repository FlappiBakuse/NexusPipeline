using System.Runtime.Versioning;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class WorkCleanupTests(ITestOutputHelper output)
{
    [Theory(DisplayName = "Actual Committed Owned Backups Use The Existing Cleanup Journal And Preserve Installation History")]
    [InlineData(PayloadAction.Upgrade)]
    [InlineData(PayloadAction.Remove)]
    public async Task CleansOwnedBackups(PayloadAction action)
    {
        await using var fixture = await WorkCleanupFixture.CreateAsync(action);
        var owner = fixture.Original.Package.Catalog;
        var before = fixture.Producer.Latest.Export();
        var installed = new ExtensionInstalledEvidence(owner.Instance, owner.Database, owner.Contracts, fixture.Original.Roots)
            .ReadCurrent("fixture-plugin")!;
        var result = await fixture.RunAsync(Guid.NewGuid());
        Assert.False(Directory.Exists(fixture.Workspace));
        Assert.Equal(JournalPhase.Committed, new RecoveryJournalRegistry(owner.Database).ReadJournal(result.JournalId).Phase);
        Assert.All(new FileEffectOutbox(owner.Database).Read(result.JournalId), item => Assert.Equal(FileEffectState.Completed, item.State));
        Assert.Equal(before, fixture.Original.Binding().Read(fixture.Producer.Plan.JournalId, fixture.Producer.PlanReference).Latest.Export());
        Assert.Equal(installed.Metadata, new ExtensionInstalledEvidence(owner.Instance, owner.Database, owner.Contracts, fixture.Original.Roots)
            .ReadCurrent("fixture-plugin")!.Metadata);
        var repeated = await fixture.RunAsync(result.Acceptance.OperationId);
        Assert.False(repeated.Acceptance.IsNew); Assert.Equal(result.JournalId, repeated.JournalId); Assert.Equal(result.Plan, repeated.Plan);
        Assert.Equal("completed", repeated.Acceptance.Status);
        var ready = new StartupJournalRecovery(owner.Instance, owner.Contracts, owner.Database, fixture.Original.Roots).Recover(owner.Lease);
        Assert.True(ready.Current.IsReady);
    }

    [Fact(DisplayName = "The Host Sweep Waits For The Producer And The Durable Page Chain Resumes")]
    public async Task ResumesSweptPages()
    {
        await using var fixture = await WorkCleanupFixture.CreateAsync(files: 70);
        var owner = fixture.Original.Package.Catalog;
        var roots = fixture.Original.Roots;
        var original = fixture.Producer.Latest.Export();
        var foreign = Directory.CreateDirectory(Path.Combine(roots.ExtensionPayloadRoot,
            ExtensionWorkspacePath.Prefix + Guid.NewGuid().ToString("N"))).FullName;
        var sentinel = Path.Combine(foreign, "foreign.bin"); File.WriteAllBytes(sentinel, [1, 3, 5]);
        ExtensionWorkSweep Sweep() => new(owner.Database, roots, owner.Index, fixture.Original.Activator, fixture.Service());

        await Sweep().SweepAsync(1, default);
        Assert.True(Directory.Exists(fixture.Workspace));
        var registry = new RecoveryJournalRegistry(owner.Database);
        Assert.DoesNotContain(registry.ReadHistory(), item => item.Kind == JournalKind.FileCleanup);
        Assert.True(await fixture.Service().RunNextPageAsync(fixture.Producer.Plan.JournalId, default));
        var first = Assert.Single(registry.ReadHistory(), item => item.Kind == JournalKind.FileCleanup);
        var firstBytes = new JournalStore(owner.Instance, owner.Contracts).Read(first.JournalId).Export();

        for (var page = 0; page < 3 && Directory.Exists(fixture.Workspace); page++)
            Assert.True(await fixture.Service().RunNextPageAsync(fixture.Producer.Plan.JournalId, default));
        Assert.False(Directory.Exists(fixture.Workspace));
        Assert.Equal(firstBytes, new JournalStore(owner.Instance, owner.Contracts).Read(first.JournalId).Export());
        Assert.Equal(original, fixture.Original.Binding().Read(fixture.Producer.Plan.JournalId, fixture.Producer.PlanReference).Latest.Export());
        Assert.Equal(new byte[] { 1, 3, 5 }, File.ReadAllBytes(sentinel));
    }

    [Fact(DisplayName = "A Real Three Hundred File Backup Uses Bounded Pages And Only The Final Page Removes Its Workspace")]
    public async Task PagesBackupCleanup()
    {
        var clock = Stopwatch.StartNew();
        var fixture = await WorkCleanupFixture.CreateAsync(files: 300);
        var preparationMilliseconds = clock.Elapsed.TotalMilliseconds;
        var productPages = new List<double>();
        double assertionsMilliseconds = 0;
        var pages = 0;
        try
        {
            var owner = fixture.Original.Package.Catalog;
            var first = await RunPage(null, null);
            var assertions = Stopwatch.StartNew();
            Assert.True(Directory.Exists(fixture.Workspace));
            Assert.Equal(WorkCleanupService.PageNodeCount, new FileEffectOutbox(owner.Database).Read(first.JournalId).Count);
            assertionsMilliseconds += assertions.Elapsed.TotalMilliseconds;
            var previous = first; pages = 1;
            while (Directory.Exists(fixture.Workspace))
            {
                Assert.InRange(pages, 1, ExtensionWorkHistory.MaximumPages - 1);
                var next = await RunPage(previous.JournalId, previous.Plan);
                assertions.Restart();
                var plan = new CleanupPlanCodec(owner.Instance, owner.Contracts, new(owner.Instance, owner.Contracts),
                    owner.Database, fixture.Original.Roots).Read(next.Plan);
                Assert.InRange(FileCleanupItems.Steps(plan).Count, 1, WorkCleanupService.PageNodeCount);
                Assert.Equal(previous.JournalId, plan.ExtensionWork!.Page.PreviousJournalId);
                Assert.Equal(previous.Plan, plan.ExtensionWork.Page.PreviousPlan);
                Assert.All(new FileEffectOutbox(owner.Database).Read(next.JournalId), item => Assert.Equal(FileEffectState.Completed, item.State));
                assertionsMilliseconds += assertions.Elapsed.TotalMilliseconds;
                previous = next; pages++;
            }
            Assert.True(pages > 2);

            async ValueTask<CleanupAcceptanceResult> RunPage(Guid? previousId, BlobReference? previousPlan)
            {
                var product = Stopwatch.StartNew();
                try { return await fixture.RunAsync(Guid.NewGuid(), previousId, previousPlan); }
                finally { productPages.Add(product.Elapsed.TotalMilliseconds); }
            }
        }
        finally
        {
            var cleanup = Stopwatch.StartNew();
            try { await fixture.DisposeAsync(); }
            finally
            {
                output.WriteLine(JsonSerializer.Serialize(new { measurement = "extension-work-300-segments-v1", files = 300,
                    preparationMilliseconds, productPageMilliseconds = productPages,
                    productMilliseconds = productPages.Sum(), assertionsMilliseconds,
                    fixtureDisposalMilliseconds = cleanup.Elapsed.TotalMilliseconds, totalMilliseconds = clock.Elapsed.TotalMilliseconds,
                    pages, scope = "Product page includes preparation/admission/cleanup/publication; this is not pure file deletion time" }));
            }
        }
    }

    [Theory(DisplayName = "Unknown Extra Changed Or Replaced Work Objects Are Not Adopted Or Deleted")]
    [InlineData("extra")]
    [InlineData("bytes")]
    [InlineData("identity")]
    public async Task PreservesUnknownObjects(string change)
    {
        await using var fixture = await WorkCleanupFixture.CreateAsync();
        var owner = fixture.Original.Package.Catalog;
        var file = Directory.EnumerateFiles(Path.Combine(fixture.Workspace, "before"), "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).First();
        if (change == "extra") file = Path.Combine(fixture.Workspace, "unowned.bin");
        else if (change == "identity") File.Delete(file);
        File.WriteAllBytes(file, [9, 7, 3, 1]);
        var identity = PhysicalObjectIdentity.Read(file); var bytes = File.ReadAllBytes(file);
        var journals = new RecoveryJournalRegistry(owner.Database).ReadSnapshot().Journals.Count;
        await Assert.ThrowsAnyAsync<IOException>(async () => await fixture.RunAsync(Guid.NewGuid()));
        Assert.Equal(journals, new RecoveryJournalRegistry(owner.Database).ReadSnapshot().Journals.Count);
        Assert.Equal(bytes, File.ReadAllBytes(file)); Assert.Equal(identity, PhysicalObjectIdentity.Read(file));
        Assert.Equal(fixture.Producer.Latest.Export(), fixture.Original.Binding().Read(fixture.Producer.Plan.JournalId).Latest.Export());
    }

    [Fact(DisplayName = "A Real Reference Blocks Admission And An Active Installed Scope Is Not Implicitly Stopped For Cleanup")]
    public async Task RespectsActiveReferences()
    {
        await using (var fixture = await WorkCleanupFixture.CreateAsync())
        {
            var owner = fixture.Original.Package.Catalog;
            var file = Directory.EnumerateFiles(Path.Combine(fixture.Workspace, "before"), "*", SearchOption.AllDirectories).First();
            var id = PhysicalObjectIdentity.Read(file);
            using (var reference = owner.Resources.TryAcquire(Guid.NewGuid(),
                [new(ExtensionWorkClaims.ObjectKey(id), ResourceClaimMode.Reference)]).Lease)
            {
                Assert.NotNull(reference);
                var error = await Assert.ThrowsAsync<IOException>(async () => await fixture.RunAsync(Guid.NewGuid()));
                Assert.Equal("extension_work_cleanup_resource_unavailable", error.Message); Assert.True(File.Exists(file));
            }
            _ = await fixture.RunAsync(Guid.NewGuid()); Assert.False(Directory.Exists(fixture.Workspace));
        }
        await using var active = await WorkCleanupFixture.CreateAsync(enabled: true);
        var current = active.Producer.Commit!.ActivationId!.Value;
        var blocked = await Assert.ThrowsAsync<IOException>(async () => await active.RunAsync(Guid.NewGuid()));
        Assert.Equal("extension_work_scope_referenced", blocked.Message);
        Assert.True(active.Original.Activator.IsPublished("fixture-plugin", current));
        Assert.False(File.Exists(Path.Combine(active.Upgrade.NextActivationDirectory, "cleaned.json")));
    }

    [Fact(DisplayName = "Final Cleanup Sql Rollback Preserves The Physical Commit And The Same Actual Owner Reconciles Without Another Deletion")]
    public async Task ReconcilesCommittedCleanup()
    {
        await using var fixture = await WorkCleanupFixture.CreateAsync();
        var owner = fixture.Original.Package.Catalog;
        var operationId = Guid.NewGuid(); var plan = fixture.DirectPage(operationId);
        using var root = owner.Resources.TryAcquire(operationId, FileCleanupItems.Claims(plan)).Lease!;
        Assert.NotNull(root);
        using var prepared = await PreparedWorkCleanup.PrepareAsync(owner.Instance, owner.Database, owner.Lease,
            owner.Contracts, fixture.Original.Roots, owner.Resources, fixture.Original.Activator, root, plan, "local", true, default);
        var accepted = new FileCleanupAcceptance(owner.Instance, owner.Contracts, owner.Database, payloadRoots: fixture.Original.Roots)
            .AcceptPrepared(prepared.Identity, prepared, DateTimeOffset.UtcNow);
        Sql(owner.Database, "CREATE TRIGGER stop_work_commit BEFORE UPDATE OF phase ON operation_journals " +
            "WHEN NEW.operation_kind='fileCleanup' AND NEW.phase='committed' BEGIN SELECT RAISE(ABORT,'controlled_work_commit'); END;");
        var executor = new OwnedCleanupExecutor(owner.Instance, owner.Contracts, owner.Database, owner.Index);
        Assert.Throws<SqliteException>(() => executor.ExecutePrepared(accepted.JournalId, owner.Lease, root,
            fixture.Original.Roots, prepared.Execution, null));
        Assert.False(Directory.Exists(fixture.Workspace));
        var physical = new JournalStore(owner.Instance, owner.Contracts).Read(accepted.JournalId);
        Assert.Equal(JournalPhase.Committed, physical.Phase);
        Assert.Equal(JournalPhase.Applied, new RecoveryJournalRegistry(owner.Database).ReadJournal(accepted.JournalId).Phase);
        var bytes = physical.Export();
        Sql(owner.Database, "DROP TRIGGER stop_work_commit;");
        Assert.Equal(FileCleanupResult.AlreadyCompleted, executor.ExecutePrepared(accepted.JournalId, owner.Lease, root,
            fixture.Original.Roots, prepared.Execution, null));
        Assert.Equal(bytes, new JournalStore(owner.Instance, owner.Contracts).Read(accepted.JournalId).Export());
        Assert.Equal(physical.Revision, new RecoveryJournalRegistry(owner.Database).ReadJournal(accepted.JournalId).Revision);
    }

    [Fact(DisplayName = "A Changed Immutable Work Proof Cannot Pass The Original Cleanup Plan Or Publish Any Removal")]
    public async Task RejectsChangedProofs()
    {
        await using var fixture = await WorkCleanupFixture.CreateAsync();
        var owner = fixture.Original.Package.Catalog;
        var plan = fixture.DirectPage(Guid.NewGuid());
        var reference = plan.ExtensionWork!.Origin.Reference;
        var path = Path.Combine(owner.Instance.RuntimeRoot, reference.RelativePath);
        var document = JsonSerializer.SerializeToUtf8Bytes(new { altered = true });
        File.WriteAllBytes(path, document);
        var snapshot = fixture.Upgrade.Scan(fixture.Workspace);
        using var root = owner.Resources.TryAcquire(plan.OperationId, FileCleanupItems.Claims(plan)).Lease!;
        Assert.NotNull(root);
        await Assert.ThrowsAnyAsync<IOException>(async () => await PreparedWorkCleanup.PrepareAsync(
            owner.Instance, owner.Database, owner.Lease, owner.Contracts, fixture.Original.Roots, owner.Resources,
            fixture.Original.Activator, root, plan, "local", true, default));
        Assert.Equal(snapshot.Entries, fixture.Upgrade.Scan(fixture.Workspace).Entries);
        Assert.Equal(document, File.ReadAllBytes(path));
    }

    private static void Sql(StateDatabase database, string sql)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath);
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
}
