using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CleanupAdmissionTests
{
    private static readonly ResourceClaim[] CatalogClaims =
        [new("extensions:catalog", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference)];

    [Theory(DisplayName = "An Exactly Guarded Content Cleanup Preserves Other Isolations And Admits Unrelated Catalog Reads During Inventory")]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopesGuardedAdmission(bool priorIsolation)
    {
        using var fixture = new Fixture(priorIsolation: priorIsolation);
        var observations = 0;
        var references = new BlobReferenceIndex(fixture.Instance, new(), fixture.Database, fixture.Blobs, fixture.Roots)
        {
            InventoryRead = () =>
            {
                observations++;
                Assert.True(fixture.Index.Current.IsReady);
                var publication = Task.Run(() => fixture.Lease.PublishExecution(() => true));
                Assert.True(publication.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(publication.GetAwaiter().GetResult());
                Assert.All(fixture.Plan.Items, item => Assert.Contains(fixture.Index.Current.Isolations,
                    isolation => isolation.JournalId == fixture.Plan.JournalId &&
                        isolation.ResourceKey == CleanupCandidateFiles.ResourceKey(item.Blob)));
                if (fixture.OtherJournal is { } other)
                    Assert.Contains(fixture.Index.Current.Isolations, isolation => isolation.JournalId == other);
                using var unrelated = fixture.Resources.TryAcquire(Guid.NewGuid(), CatalogClaims).Lease;
                Assert.NotNull(unrelated);
                Assert.Null(fixture.Resources.TryAcquire(Guid.NewGuid(), FileCleanupItems.Claims(fixture.Plan)).Lease);
                foreach (var item in fixture.Plan.Items)
                    Assert.Throws<IOException>(() => { using var input = fixture.Blobs.OpenVerified(item.Blob); });
            }
        };
        var cleanup = new BlobCleanup(fixture.Instance, fixture.Blobs, references);
        Assert.Equal(FileCleanupResult.Cleaned, fixture.Execute(cleanup));
        Assert.Equal(1, observations);
        Assert.All(fixture.Plan.Items, item => Assert.False(File.Exists(fixture.PathOf(item.Blob))));
        Assert.True(fixture.Index.Current.IsReady);
        Assert.DoesNotContain(fixture.Index.Current.Isolations, item => item.JournalId == fixture.Plan.JournalId);
        if (fixture.OtherJournal is { } remaining)
            Assert.Contains(fixture.Index.Current.Isolations, item => item.JournalId == remaining);
    }

    [Theory(DisplayName = "Missing Candidate Protection Or Execution Authority Retains The Existing Global Cleanup Gate")]
    [InlineData("reader")]
    [InlineData("bytes")]
    [InlineData("unknown")]
    [InlineData("writer")]
    [InlineData("roots")]
    [InlineData("archive")]
    public void KeepsUnverifiedAdmissionBlocked(string fault)
    {
        using var fixture = new Fixture(count: fault == "archive" ? 1 : 2);
        FileStream? reader = fault == "reader" ? fixture.Blobs.OpenVerified(fixture.Plan.Items[0].Blob) : null;
        try
        {
            if (fault == "bytes") File.WriteAllBytes(fixture.PathOf(fixture.Plan.Items[0].Blob), [9]);
            if (fault == "unknown") fixture.Index.BlockUnknown("fixture_external_ownership_unknown");
            if (fault == "writer")
            {
                var writer = JournalWriterStore.CurrentWriter;
                fixture.Database.Write((connection, transaction) =>
                {
                    JournalWriterStore.Insert(connection, transaction, fixture.Plan.JournalId,
                        new(Guid.NewGuid(), writer.Process with { ImageSha256 = new string('0', 64) }));
                    return true;
                });
            }
            var observations = 0;
            var references = new BlobReferenceIndex(fixture.Instance, new(), fixture.Database, fixture.Blobs, fixture.Roots)
            {
                InventoryRead = () =>
                {
                    observations++;
                    Assert.False(fixture.Index.Current.IsReady);
                    Assert.Null(fixture.Resources.TryAcquire(Guid.NewGuid(), CatalogClaims).Lease);
                    reader?.Dispose(); reader = null;
                }
            };
            var cleanup = new BlobCleanup(fixture.Instance, fixture.Blobs, references);
            using var held = fault == "archive" ? PhysicalObjectDeletion.HoldFile(
                BlobCleanup.Intent(fixture.Plan.Items[0], fixture.PathOf(fixture.Plan.Items[0].Blob))) : null;
            var result = fixture.Execute(cleanup, fault == "roots" ? null : fixture.Roots, held);
            Assert.True(observations > 0);
            if (fault is "bytes" or "writer")
            {
                Assert.Equal(FileCleanupResult.Quarantined, result);
                Assert.All(fixture.Plan.Items, item => Assert.True(File.Exists(fixture.PathOf(item.Blob))));
            }
            else Assert.Equal(FileCleanupResult.Cleaned, result);
        }
        finally { reader?.Dispose(); }
    }

    [Fact(DisplayName = "A Rejected Admission Publication Preserves Every Cleanup Candidate And Never Replays Inventory")]
    public void PreservesRejectedPublication()
    {
        using var fixture = new Fixture();
        fixture.Database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "CREATE TRIGGER fixture_reject_cleanup_publication BEFORE UPDATE OF ready ON main.recovery_index_state " +
                "WHEN NEW.ready=1 BEGIN SELECT RAISE(ABORT,'fixture_cleanup_publication_rejected'); END;";
            command.ExecuteNonQuery(); return true;
        });
        var observations = 0;
        var references = new BlobReferenceIndex(fixture.Instance, new(), fixture.Database, fixture.Blobs, fixture.Roots)
            { InventoryRead = () => observations++ };
        var cleanup = new BlobCleanup(fixture.Instance, fixture.Blobs, references);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => fixture.Execute(cleanup));
        Assert.Equal(0, observations);
        Assert.False(fixture.Index.Current.IsReady);
        Assert.All(fixture.Plan.Items, item => Assert.True(File.Exists(fixture.PathOf(item.Blob))));
        Assert.Null(fixture.Resources.TryAcquire(Guid.NewGuid(), CatalogClaims).Lease);
        Assert.Equal(JournalPhase.Prepared, new RecoveryJournalRegistry(fixture.Database).ReadJournal(fixture.Plan.JournalId).Phase);
    }

    [Fact(DisplayName = "Cleanup Reconciliation Waits For The Existing Publication Owner And Preserves Its Independent Registration")]
    public async Task DefersCleanupReconciliation()
    {
        using var fixture = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var enteredAsync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? competing = null;
        Guid otherJournal = Guid.Empty;
        var observer = new CleanupObserver(() =>
        {
            competing = Task.Run(() => fixture.Lease.PublishExecution(() =>
            {
                entered.Set();
                enteredAsync.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                otherJournal = fixture.RegisterIndependentCleanup();
                new RecoveryCoordinator(new RecoveryJournalRegistry(fixture.Database),
                    new AdmissionPublicationEvidence(fixture.Instance, fixture.Database, new(), fixture.Roots), fixture.Index).VerifyAndPublish();
                return true;
            }));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        }, CleanupPersistencePoint.FileRemoved);
        var blobs = new BlobCleanup(fixture.Instance, fixture.Blobs,
            new(fixture.Instance, new(), fixture.Database, fixture.Blobs, fixture.Roots));
        var cleanup = Task.Run(() => fixture.Execute(blobs, fixture.Roots, null, observer));
        bool completedWhileOwned;
        try
        {
            await enteredAsync.Task.WaitAsync(TimeSpan.FromSeconds(10));
            completedWhileOwned = await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(2))) == cleanup;
        }
        finally { release.Set(); }
        var result = await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(competing); Assert.True(await competing!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(FileCleanupResult.Cleaned, result);
        Assert.False(completedWhileOwned);
        Assert.True(fixture.Index.Current.IsReady);
        Assert.NotEqual(Guid.Empty, otherJournal);
        Assert.Contains(fixture.Index.Current.Isolations, item => item.JournalId == otherJournal);
        Assert.DoesNotContain(fixture.Index.Current.Isolations, item => item.JournalId == fixture.Plan.JournalId);
        Assert.All(fixture.Plan.Items, item => Assert.False(File.Exists(fixture.PathOf(item.Blob))));
    }

    [Fact(DisplayName = "Unscoped Cleanup Cannot Block Admission While Another Publication Owns The Recovery Index")]
    public async Task DefersCleanupAdmissionBlock()
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var publication = Task.Run(() => fixture.Lease.PublishExecution(() =>
        {
            entered.SetResult();
            release.Wait(deadline.Token);
        }));
        Task<FileCleanupResult>? cleanup = null;
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            var blobs = new BlobCleanup(fixture.Instance, fixture.Blobs,
                new(fixture.Instance, new(), fixture.Database, fixture.Blobs, fixture.Roots));
            cleanup = Task.Run(() => fixture.Execute(blobs, null, null));
            while (fixture.Lease.ExecutionPublicationCount < 2) await Task.Delay(10, deadline.Token);
            Assert.True(fixture.Index.Current.IsReady);
        }
        finally
        {
            release.Set();
            await publication.WaitAsync(deadline.Token);
            if (cleanup is not null) Assert.Equal(FileCleanupResult.Cleaned, await cleanup.WaitAsync(deadline.Token));
        }
        Assert.True(fixture.Index.Current.IsReady);
    }

    [Fact(DisplayName = "A Failed Cleanup Publication Does Not Replace A Newer Verified Recovery Index")]
    public async Task PreservesNewerPublication()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? blocker = null;
        var observer = new CleanupObserver(() =>
        {
            Sql("CREATE TRIGGER fixture_reject_cleanup_publication BEFORE UPDATE OF ready ON main.recovery_index_state " +
                "WHEN NEW.ready=1 BEGIN SELECT RAISE(ABORT,'fixture_cleanup_publication_rejected'); END;");
            blocker = Task.Run(() => fixture.Lease.PublishExecution(() =>
            {
                entered.SetResult();
                release.Wait(deadline.Token);
            }));
            while (fixture.Lease.ExecutionPublicationCount < 2)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Thread.Sleep(10);
            }
        }, CleanupPersistencePoint.SqlProjectionCommitted, JournalPhase.Committed);
        var blobs = new BlobCleanup(fixture.Instance, fixture.Blobs,
            new(fixture.Instance, new(), fixture.Database, fixture.Blobs, fixture.Roots));
        var cleanup = Task.Run(() => fixture.Execute(blobs, fixture.Roots, null, observer));
        Task<RecoveryProjection>? verification = null;
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            while (fixture.Lease.ExecutionPublicationCount < 2) await Task.Delay(10, deadline.Token);
            verification = Task.Run(() => fixture.Lease.PublishExecution(() =>
            {
                Sql("DROP TRIGGER main.fixture_reject_cleanup_publication;");
                return new RecoveryCoordinator(new RecoveryJournalRegistry(fixture.Database),
                    new AdmissionPublicationEvidence(fixture.Instance, fixture.Database, new(), fixture.Roots), fixture.Index).VerifyAndPublish();
            }));
            while (fixture.Lease.ExecutionPublicationCount < 3) await Task.Delay(10, deadline.Token);
        }
        finally
        {
            release.Set();
            if (blocker is not null) await blocker.WaitAsync(deadline.Token);
            Assert.Equal(FileCleanupResult.Cleaned, await cleanup.WaitAsync(deadline.Token));
        }
        Assert.NotNull(verification);
        Assert.Same(await verification.WaitAsync(deadline.Token), fixture.Index.Current);
        Assert.True(fixture.Index.Current.IsReady);

        void Sql(string statement) => fixture.Database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = statement;
            return command.ExecuteNonQuery();
        });
    }

    [Fact(DisplayName = "Actual Run Registration And Initial Journal Materialization Share Their Publication Boundary")]
    public void SerializesRunAcceptance()
    {
        using var fixture = new ExecutionAdmissionFixture();
        var observations = 0;
        var service = fixture.NewService(new AcceptanceObserver(() =>
        {
            observations++;
            var error = Assert.Throws<IOException>(() => fixture.Source.Lease.PublishExecution(() => true));
            Assert.Equal("execution_publication_reentrancy", error.Message);
        }));
        var operation = Guid.NewGuid();
        var accepted = service.Start(operation, "cleanup-publication-fixture",
            fixture.Source.Request(new BindingTarget(fixture.Source.BindingA)), PlanBuilderFixture.CreatedAt);
        Assert.True(accepted.Acceptance.IsNew); Assert.Equal(2, observations);
        Assert.True(fixture.Index.Current.IsReady); Assert.Equal(1, fixture.Inbox.PendingCount);
        using var owner = fixture.Take(operation);
        Assert.Equal(accepted.JournalId, owner.Receipt.JournalId);
        var registered = new RecoveryJournalRegistry(fixture.Source.Database).ReadJournal(accepted.JournalId);
        Assert.Equal(JournalPhase.Prepared, registered.Phase); Assert.Equal(1, registered.Revision);
    }

    [Fact(DisplayName = "Each Cleanup Journal Flush And SQL Projection Excludes Another Publication While Inventory Stays Outside")]
    public void SerializesCleanupPersistence()
    {
        using var fixture = new Fixture();
        var observations = 0;
        var observer = new PersistenceObserver(() =>
        {
            observations++;
            var error = Assert.Throws<IOException>(() => fixture.Lease.PublishExecution(() => true));
            Assert.Equal("execution_publication_reentrancy", error.Message);
        });
        var cleanup = new BlobCleanup(fixture.Instance, fixture.Blobs,
            new(fixture.Instance, new(), fixture.Database, fixture.Blobs, fixture.Roots));
        Assert.Equal(FileCleanupResult.Cleaned, fixture.Execute(cleanup, fixture.Roots, null, observer));
        Assert.True(observations > 0); Assert.True(fixture.Index.Current.IsReady);
        Assert.All(fixture.Plan.Items, item => Assert.False(File.Exists(fixture.PathOf(item.Blob))));
    }

    private sealed class CleanupObserver(Action observe, CleanupPersistencePoint selected, JournalPhase? expected = null) : ICleanupPersistenceObserver
    {
        private int _observed;
        public void Reached(CleanupPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            if (point == selected && (expected is null || phase == expected) && Interlocked.Exchange(ref _observed, 1) == 0) observe();
        }
    }

    private sealed class PersistenceObserver(Action observe) : ICleanupPersistenceObserver
    {
        public void Reached(CleanupPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            if (point is CleanupPersistencePoint.JournalFlushed or CleanupPersistencePoint.SqlProjectionCommitted) observe();
        }
    }

    private sealed class AcceptanceObserver(Action observe) : IAttemptPersistenceObserver
    {
        public void Reached(AttemptPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            if (point is AttemptPersistencePoint.AcceptanceCommitted or AttemptPersistencePoint.JournalCreated) observe();
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal RuntimeInstance Instance { get; }
        internal StateDatabase Database { get; }
        internal InstanceLease Lease { get; }
        internal PayloadApplyRoots Roots { get; }
        internal ImmutableBlobStore Blobs { get; }
        internal RecoveryIndex Index { get; } = new();
        internal ResourceLeaseManager Resources { get; }
        internal FrozenFileCleanup Plan { get; }
        internal Guid? OtherJournal { get; }
        private readonly ResourceOperationLease _root;

        internal Fixture(int count = 2, bool priorIsolation = false)
        {
            var path = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
            Instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(path, "runtime"));
            Database = StateDatabase.CreateFresh(Instance);
            Lease = InstanceLease.Acquire(Instance, new());
            Roots = new(Path.Combine(path, "host"), Path.Combine(Instance.RuntimeRoot, "extensions"));
            Blobs = new(Instance, new());
            if (priorIsolation)
            {
                var other = new CleanupPlanCodec(Instance, new(), Blobs).Capture(Guid.NewGuid(), Guid.NewGuid(),
                    [Blobs.PersistContent(new byte[] { 99, 100 }, default)]);
                Accept(other); OtherJournal = other.JournalId;
            }
            new RecoveryCoordinator(new RecoveryJournalRegistry(Database),
                new AdmissionPublicationEvidence(Instance, Database, new(), Roots), Index).VerifyAndPublish();
            Resources = new(new(Index, TimeProvider.System));
            var candidates = Enumerable.Range(0, count).Select(index => Blobs.PersistContent(BitConverter.GetBytes(index), default)).ToArray();
            Plan = new CleanupPlanCodec(Instance, new(), Blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), candidates);
            _root = Assert.IsType<ResourceOperationLease>(Resources.TryAcquire(Plan.OperationId, FileCleanupItems.Claims(Plan)).Lease);
            Accept(Plan);
        }

        private void Accept(FrozenFileCleanup plan) => new FileCleanupAcceptance(Instance, new(), Database, payloadRoots: Roots)
            .Accept(new(plan.OperationId, "fixture-subject", new string('a', 64), "file.cleanup"), plan, DateTimeOffset.UtcNow);

        internal Guid RegisterIndependentCleanup()
        {
            var plan = new CleanupPlanCodec(Instance, new(), Blobs).Capture(Guid.NewGuid(), Guid.NewGuid(),
                [Blobs.PersistContent(new byte[] { 91, 92 }, default)]);
            Accept(plan); return plan.JournalId;
        }

        internal string PathOf(BlobReference blob) => PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(Instance.RuntimeRoot, blob.RelativePath));

        internal FileCleanupResult Execute(BlobCleanup cleanup) => Execute(cleanup, Roots, null);

        internal FileCleanupResult Execute(BlobCleanup cleanup, PayloadApplyRoots? roots, PhysicalObjectDeletion.FileLease? held,
            ICleanupPersistenceObserver? observer = null)
        {
            using var borrow = _root.Borrow(Resources, Plan.OperationId, FileCleanupItems.Claims(Plan));
            return borrow.Use(() =>
            {
                // Existing reference and persistence observers expose the public Execute core without replacing its authority or file operations.
                var method = typeof(OwnedCleanupExecutor).GetMethod("ExecuteCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
                try
                {
                    return (FileCleanupResult)method.Invoke(new OwnedCleanupExecutor(Instance, new(), Database, Index),
                        [Plan.JournalId, Lease, _root, roots, null, held, observer, null, cleanup])!;
                }
                catch (TargetInvocationException error) when (error.InnerException is not null)
                { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
            });
        }

        public void Dispose() { _root.Dispose(); Lease.Dispose(); }
    }
}
