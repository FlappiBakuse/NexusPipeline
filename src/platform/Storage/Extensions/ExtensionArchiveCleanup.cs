using System.Runtime.Versioning;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ExtensionArchiveCleanup(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ContractReader contracts, PayloadApplyRoots roots, ResourceLeaseManager resources, RecoveryIndex recovery)
{
    internal const string OperationKind = "extension.archive.retire";
    internal const string ContentOperationKind = "extension.content.retire";
    internal const int ScanPageSize = 16;
    private const string Subject = "host:extension-archive";
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);
    private readonly RecoveryJournalRegistry _registry = new(database);
    private string _after = "";
    private DateTimeOffset _nextScan;
    private long _scanRevision;
    private long? _completedRevision;
    private bool _reported;
    private readonly Queue<Guid> _producers = new();
    private readonly Queue<(Guid Producer, BlobReference Content)> _candidates = new();
    private BlobReferenceScan? _referenceScan;

    internal void Sweep(CancellationToken cancellation, ProcessIdentityLease? executor = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!recovery.Current.IsReady || DateTimeOffset.UtcNow < _nextScan) return;
        if (_completedRevision == database.CommitSequence) return;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.All))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.operations WHERE operation_kind='execution.start' AND status='accepted') " +
                "OR EXISTS(SELECT 1 FROM notifications.notification_delivery_pending) " +
                "OR EXISTS(SELECT 1 FROM notifications.notification_receipt_progress WHERE next_ordinal<route_count);";
            if ((long)command.ExecuteScalar()! != 0) return;
        }
        if (_after.Length == 0 && _producers.Count == 0 && _candidates.Count == 0) _scanRevision = database.CommitSequence;
        using var paths = DirectoryAttributeScope.Hold(instance);
        using var reads = BlobReadScope.Hold(instance);
        using var parents = JournalReadScope.HoldParents(instance, contracts, database.AttemptReads);
        var started = Stopwatch.GetTimestamp();
        var budget = new ReferenceReadBudget(TimeSpan.FromMilliseconds(50), 2097152);
        try
        {
            var installed = ExtensionInstalledRows.ReadContentDigests(database);
            for (var scanned = 0; _referenceScan is null && _candidates.Count < 256 && scanned < ScanPageSize; scanned++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (_producers.Count == 0)
                {
                    foreach (var item in ReadPage()) _producers.Enqueue(item);
                    if (_producers.Count == 0)
                    {
                        if (_candidates.Count == 0) { _after = ""; _completedRevision = _scanRevision; return; }
                        break;
                    }
                }
                var producer = _producers.Dequeue(); _after = producer.ToString("D");
                foreach (var content in ReadCandidates(producer))
                    if (!installed.Contains(content.Sha256) && File.Exists(Path.Combine(instance.RuntimeRoot, content.RelativePath)))
                        _candidates.Enqueue((producer, content));
            }
            var candidates = _candidates.Take(256).ToArray();
            if (candidates.Length > 0)
            {
                var selected = candidates.Select(item => item.Content).ToHashSet();
                _referenceScan ??= new(selected);
                var referenced = new BlobReferenceIndex(instance, contracts, database, _blobs, roots)
                    { ReadBudget = budget }
                    .ReadReferenced(selected, _referenceScan);
                foreach (var (producer, content) in candidates)
                {
                    var accepted = !referenced.Contains(content) &&
                        RunNextPage(producer, content, cancellation, executor: executor)?.Acceptance.IsNew == true;
                    _candidates.Dequeue();
                    if (_candidates.Count == 0) _referenceScan = null;
                    if (accepted) return;
                }
            }
            _reported = false;
        }
        catch (ReferenceReadDeferred) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or
            FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            _referenceScan = null;
            if (!_reported) Console.Error.WriteLine($"extension_archive_cleanup_unavailable {error.GetType().Name}");
            _reported = true; _nextScan = DateTimeOffset.UtcNow.AddMinutes(1);
        }
        finally
        {
            // A protected graph is indivisible; cool down after it instead of draining missed timer ticks.
            var ticks = Math.Max(TimeSpan.FromSeconds(5).Ticks, Stopwatch.GetElapsedTime(started).Ticks * 49);
            ticks = Math.Max(ticks, checked((long)(budget.ObservedReadBytes * (double)TimeSpan.TicksPerSecond / 262144)));
            var next = DateTimeOffset.UtcNow.AddTicks(ticks);
            if (next > _nextScan) _nextScan = next;
        }
    }

    private IReadOnlyList<Guid> ReadPage()
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT j.journal_id FROM main.operation_journals j JOIN main.operations o " +
            "ON o.operation_id=j.operation_id AND o.initial_journal_id=j.journal_id " +
            "WHERE j.journal_id>$after AND j.operation_kind='extensionApply' AND j.phase='committed' " +
            "AND o.operation_kind='extension.apply' AND o.status='completed' ORDER BY j.journal_id LIMIT $limit;";
        command.Parameters.AddWithValue("$after", _after);
        command.Parameters.AddWithValue("$limit", ScanPageSize);
        var result = new List<Guid>();
        using var rows = command.ExecuteReader();
        while (rows.Read()) result.Add(Guid.ParseExact(rows.GetString(0), "D"));
        return result;
    }

    private BlobReferenceIndex References() => new(instance, contracts, database, _blobs, roots);

    internal BlobReference? ReadCandidate(Guid producer)
        => ReadOrigin(producer).Source.Package?.Archive;

    private (FrozenPayloadApply Plan, ExtensionPayloadSource Source) ReadOrigin(Guid producer)
    {
        var registered = _registry.ReadJournal(producer);
        if (registered is not { Kind: JournalKind.ExtensionApply, Phase: JournalPhase.Committed }) throw Invalid();
        var full = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, registered.PlanPath));
        var reference = new BlobReference(registered.PlanPath, new FileInfo(full).Length, registered.PlanDigest);
        var plan = new PayloadPlanCodec(instance, contracts, _blobs, roots).ReadStructure(reference, out var source);
        if (plan.Kind != registered.Kind || plan.OperationId != registered.OperationId || plan.JournalId != producer ||
            !plan.ResourceKeys.SequenceEqual(registered.ResourceKeys) || source is null) throw Invalid();
        return (plan, source);
    }

    internal IReadOnlyList<BlobReference> ReadCandidates(Guid producer)
    {
        var origin = ReadOrigin(producer);
        return (origin.Source.Package is { } package ? new[] { package.Archive } : [])
            .Concat(origin.Plan.Before.Contents).Concat(origin.Plan.Candidate.Contents).Distinct().ToArray();
    }

    internal CleanupAcceptanceResult? RunNextPage(Guid producer, BlobReference archive, CancellationToken cancellation,
        ICleanupPersistenceObserver? observer = null, ProcessIdentityLease? executor = null)
    {
        lease.VerifyHeld(instance);
        if (!resources.UsesRecoveryIndex(recovery) || !ReadCandidates(producer).Contains(archive)) throw Invalid();
        Guid? previous = null;
        FrozenCleanupItem? original = null;
        for (var attempt = 0; attempt < 256; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            var identity = Identity(instance, producer, archive, previous,
                ReadCandidate(producer) == archive ? OperationKind : ContentOperationKind);
            var receipt = ReadReceipt(identity, archive);
            if (receipt is null)
                return AcceptPage(producer, archive, identity, original, cancellation, observer, executor);
            if (receipt.State != FileEffectState.NotExecuted) return receipt.Result;
            previous = receipt.Result.JournalId;
            original = receipt.Item;
        }
        throw Invalid();
    }

    private CleanupAcceptanceResult? AcceptPage(Guid producer, BlobReference archive, OperationIdentity identity,
        FrozenCleanupItem? previous, CancellationToken cancellation, ICleanupPersistenceObserver? observer, ProcessIdentityLease? executor)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, archive.RelativePath));
        if (!File.Exists(path)) return null;
        var item = new FrozenCleanupItem("remove-000", archive, PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!),
            PhysicalObjectIdentity.Read(path));
        if (previous is not null && previous != item) throw Invalid();
        var objectKey = $"filesystem:object:{item.Identity.VolumeSerial}:{item.Identity.FileId}";
        var claims = PhysicalResourceAdmission.Merge(PhysicalResourceAdmission.Read(path, includeDescendants: false)
            .Append(new(CleanupCandidateFiles.ResourceKey(archive), ResourceClaimMode.Mutation))
            .Append(new("blobs:content", ResourceClaimMode.Mutation)));
        if (!claims.Contains(new ResourceClaim(objectKey, ResourceClaimMode.Mutation))) throw Invalid();
        using var root = resources.TryAcquire(identity.OperationId, claims).Lease;
        if (root is null) return null;
        RequireClosedOrigin(producer, archive);
        using var held = PhysicalObjectDeletion.HoldFile(BlobCleanup.Intent(item, path));
        var cleanup = new BlobCleanup(instance, _blobs, References());
        var state = cleanup.Inspect(item, held);
        if (state == CleanupItemState.Referenced) return null;
        if (state != CleanupItemState.VerifiedUnreferenced) throw Invalid();
        cancellation.ThrowIfCancellationRequested();
        var plan = new FrozenFileCleanup(instance.InstanceId, identity.OperationId, Guid.NewGuid(), Array.AsReadOnly(new[] { item }));
        var accepted = lease.PublishExecution(() => new FileCleanupAcceptance(instance, contracts, database, observer, roots)
            .Accept(identity, plan, DateTimeOffset.UtcNow));
        if (accepted.Acceptance.IsNew)
            _ = new OwnedCleanupExecutor(instance, contracts, database, recovery)
                .ExecuteArchive(accepted.JournalId, lease, root, roots, held, cleanup, observer, executor);
        var terminal = ReadReceipt(identity, archive)?.Result ?? throw Invalid();
        return terminal with { Acceptance = terminal.Acceptance with { IsNew = accepted.Acceptance.IsNew } };
    }

    private void RequireClosedOrigin(Guid producer, BlobReference archive)
    {
        var gate = new JournalEffectGate(database).Read(producer);
        if (gate.State == EffectGateState.Entered)
        {
            var bound = new ExtensionPayloadBinding(instance, database, contracts, roots).Read(producer);
            if (bound.Registered.Phase != JournalPhase.Committed || !ReadCandidates(producer).Contains(archive)) throw Invalid();
            return;
        }
        var plan = PayloadPreparedBinding.Read(instance, contracts, database, roots, producer, verifyContents: false);
        var source = new PayloadSourceCodec(instance, contracts).ReadMetadata(plan.Plan.ExtensionSource ?? throw Invalid());
        if (gate.State != EffectGateState.Sealed || plan.Document["phase"]?.GetValue<string>() != "committed" ||
            !ReadCandidates(producer).Contains(archive)) throw Invalid();
    }

    private PageReceipt? ReadReceipt(OperationIdentity identity, BlobReference archive)
    {
        OperationAcceptance? acceptance;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Extensions | DatabaseDomain.System))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            acceptance = OperationRows.ReadExisting(connection, transaction, identity);
            transaction.Commit();
        }
        if (acceptance is null) return null;
        var registered = _registry.ReadOperationJournal(identity.OperationId);
        var reference = new BlobReference(registered.PlanPath, new FileInfo(PhysicalPathPolicy.NormalizeWithoutLinks(
            Path.Combine(instance.RuntimeRoot, registered.PlanPath))).Length, registered.PlanDigest);
        var completed = registered.Phase == JournalPhase.Committed;
        if (acceptance.Status != (completed ? "completed" : "accepted")) throw Invalid();
        var effects = new FileEffectOutbox(database).Read(registered.JournalId);
        var plan = completed
            ? CleanupJournalBinding.ReadExact(instance, contracts, registered, effects, database, roots).Plan
            : CleanupJournalBinding.ReadPlan(instance, contracts, registered, database, roots);
        if (plan.ExtensionWork is not null || plan.Items.Count != 1 || plan.Items[0].Blob != archive ||
            effects.Count != 1 || effects[0].StepId != plan.Items[0].StepId ||
            completed && effects[0].State is not (FileEffectState.Completed or FileEffectState.NotExecuted)) throw Invalid();
        return new(new(acceptance, registered.JournalId, reference), plan.Items[0], completed ? effects[0].State : null);
    }

    private static OperationIdentity Identity(RuntimeInstance instance, Guid producer, BlobReference archive, Guid? previous, string kind)
    {
        var key = FormattableString.Invariant($"{kind}/1/{instance.InstanceId:D}/{producer:D}/{archive.Sha256}/{archive.SizeBytes}/{previous:D}");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new(new Guid(digest.AsSpan(0, 16)), Subject, Convert.ToHexStringLower(digest), kind);
    }

    private sealed record PageReceipt(CleanupAcceptanceResult Result, FrozenCleanupItem Item, FileEffectState? State);
    private static IOException Invalid() => new("extension_archive_cleanup_unbound");
}
