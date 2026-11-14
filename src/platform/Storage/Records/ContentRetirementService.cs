using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Records;

public sealed record ContentRetirementPage(Guid OperationId, Guid CleanupJournalId, string LastSha256, int Count);

[SupportedOSPlatform("windows")]
public sealed class ContentRetirementService
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly RecoveryIndex _index;
    private readonly ICleanupPersistenceObserver? _observer;
    private static readonly ResourceClaim[] Claims = [
        new("records:retention", ResourceClaimMode.Mutation), new("blobs:content", ResourceClaimMode.Reference),
        new("notifications:queue", ResourceClaimMode.Mutation)];

    public ContentRetirementService(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, ResourceLeaseManager resources, RecoveryIndex index,
        ICleanupPersistenceObserver? observer = null)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(instance.RuntimeRoot, "state", "runtime.db")) ||
            !resources.UsesRecoveryIndex(index)) throw RecordRetirementCodec.Invalid();
        _instance = instance; _database = database; _contracts = contracts; _lease = lease; _resources = resources; _index = index; _observer = observer;
    }

    public ContentRetirementPage? RetireLogsPage(Guid operationId, string subjectId, Guid runId,
        string afterSha256 = "", int limit = 128)
        => RetirePage(operationId, subjectId, runId, afterSha256, limit, RetirementContentKind.Logs);

    public ContentRetirementPage? RetireImagesPage(Guid operationId, string subjectId, Guid runId,
        string afterSha256 = "", int limit = 128)
        => RetirePage(operationId, subjectId, runId, afterSha256, limit, RetirementContentKind.Images);

    internal bool CompleteCleanup(ContentRetirementPage page)
    {
        var registered = new RecoveryJournalRegistry(_database).ReadJournal(page.CleanupJournalId);
        var plan = CleanupJournalBinding.ReadPlan(_instance, _contracts, registered, _database);
        var claims = FileCleanupItems.Claims(plan);
        using var root = _resources.TryAcquire(page.OperationId, claims).Lease ?? throw new IOException("record_retirement_admission_blocked");
        using var borrowed = root.Borrow(_resources, page.OperationId, claims);
        return borrowed.Use(() => new OwnedCleanupExecutor(_instance, _contracts, _database, _index)
            .Execute(page.CleanupJournalId, _lease, root)) == FileCleanupResult.Cleaned;
    }

    internal bool RetireEmptyHistory(Guid runId, long settingsRevision, DateTimeOffset now)
    {
        _lease.VerifyHeld(_instance);
        var claims = Claims.Append(new ResourceClaim("settings:host", ResourceClaimMode.Reference)).ToArray();
        using var root = _resources.TryAcquire(Guid.NewGuid(), claims).Lease ?? throw new IOException("record_retirement_admission_blocked");
        return _lease.PublishExecution(() =>
        {
            RequireRecovery(_index.Current);
            var reader = new RetirementFamilyReader(_instance, _database, _contracts);
            var family = reader.CaptureMetadata(runId); reader.VerifyForPublication(family);
            foreach (var kind in new[] { RetirementContentKind.Logs, RetirementContentKind.Images })
                if (SelectPage(reader.Candidates(family, kind), family, "", 1, kind).Length != 0) return false;
            return _database.Write((connection, transaction) =>
            {
                var settings = HostSettingsRows.ReadCurrent(connection, transaction);
                if (settings.Revision != settingsRevision) throw new IOException("record_retirement_settings_changed");
                RecordRetirementRows.RequireSettledInside(connection, transaction, runId, family.Attempts.Length);
                using var command = RecordRetirementRows.Command(connection, transaction,
                    "SELECT o.created_at FROM main.execution_start_sources s JOIN main.operations o USING(operation_id) WHERE s.run_id=$0 AND s.attempt_ordinal=1;", runId);
                var start = DateTimeOffset.ParseExact(command.ExecuteScalar() as string ?? throw RecordRetirementCodec.Invalid(), "O",
                    System.Globalization.CultureInfo.InvariantCulture);
                var cutoff = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).Date.AddDays(1 - settings.Values.HistoryRetentionDays);
                if (TimeZoneInfo.ConvertTime(start, TimeZoneInfo.Local).Date >= cutoff) return false;
                command.CommandText = "UPDATE records.run_records SET history_retired_at_utc_ticks=$time WHERE run_id=$0 AND history_retired_at_utc_ticks IS NULL;";
                command.Parameters.AddWithValue("$time", now.UtcTicks);
                return command.ExecuteNonQuery() == 1;
            });
        });
    }

    internal Guid? ResumeAutomatic(Guid? after, long settingsRevision, DateTimeOffset cutoff)
    {
        _lease.VerifyHeld(_instance);
        var claims = Claims.Append(new ResourceClaim("settings:host", ResourceClaimMode.Reference)).ToArray();
        ContentRetirementPage? page = null;
        Guid? selected;
        using (var root = _resources.TryAcquire(Guid.NewGuid(), claims).Lease ?? throw new IOException("record_retirement_admission_blocked"))
            selected = _lease.PublishExecution(() =>
            {
                RequireRecovery(_index.Current);
                if (new HostSettingsStore(_database).Read().Revision != settingsRevision)
                    throw new IOException("record_retirement_settings_changed");
                var candidate = ReadResumable(after, cutoff);
                if (candidate is null) return (Guid?)null;
                var (operation, run, reference) = candidate.Value;
                var reader = new RetirementFamilyReader(_instance, _database, _contracts);
                var family = reader.CaptureMetadata(run);
                var document = VerifiedRecordRetirement.ReadProof(_instance, _database, _contracts, family, reference);
                if (document.SubjectId != "host:history-retention" || document.OperationId != operation)
                    throw RecordRetirementCodec.Invalid();
                reader.VerifyForPublication(family);
                var registry = new RecoveryJournalRegistry(_database);
                var original = registry.ReadJournal(document.CleanupJournalId);
                var originalPlan = CleanupJournalBinding.ReadPlan(_instance, _contracts, original, _database);
                var subject = "host:history-retention:" + operation.ToString("D");
                var current = ReadResume(subject) ?? original;
                var plan = CleanupJournalBinding.ReadPlan(_instance, _contracts, current, _database);
                if (!plan.Items.SequenceEqual(originalPlan.Items) || plan.ExtensionWork is not null)
                    throw RecordRetirementCodec.Invalid();
                if (current.JournalId != original.JournalId)
                {
                    using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
                    using var transaction = connection.BeginTransaction(deferred: true);
                    var identity = ResumeIdentity(subject, reference, current);
                    if (OperationRows.ReadExisting(connection, transaction, identity) is null) throw RecordRetirementCodec.Invalid();
                }
                if (current.Phase == JournalPhase.Committed)
                {
                    var effects = new FileEffectOutbox(_database).Read(current.JournalId);
                    var bound = CleanupJournalBinding.ReadExact(_instance, _contracts, current, effects, _database);
                    var receipts = bound.Document["receipts"]!.AsArray();
                    if (!receipts.Any(item => item!["stepId"]!.GetValue<string>() == "verify-no-effect")) return operation;
                    if (effects.Count != plan.Items.Count || effects.Any(item => item.State != FileEffectState.NotExecuted))
                        throw RecordRetirementCodec.Invalid();
                    var blobs = new ImmutableBlobStore(_instance, _contracts);
                    var held = new List<IDisposable>();
                    try
                    {
                        foreach (var item in plan.Items) held.Add(blobs.OpenVerified(item.Blob));
                        var codec = new CleanupPlanCodec(_instance, _contracts, blobs);
                        var fresh = codec.Capture(Guid.NewGuid(), Guid.NewGuid(), plan.Items.Select(item => item.Blob).ToArray());
                        if (!fresh.Items.SequenceEqual(originalPlan.Items)) throw RecordRetirementCodec.Invalid();
                        var freshReference = codec.Persist(fresh);
                        var journal = new RegisteredJournal(fresh.JournalId, fresh.OperationId, JournalKind.FileCleanup,
                            JournalPhase.Prepared, 1, freshReference.RelativePath, freshReference.Sha256, FileCleanupItems.Keys(fresh));
                        var accepted = new FileCleanupAcceptance(_instance, _contracts, _database, _observer)
                            .Accept(ResumeIdentity(subject, reference, journal), fresh, DateTimeOffset.UtcNow);
                        page = new(fresh.OperationId, accepted.JournalId, document.Contents[^1].Sha256, document.Contents.Length);
                    }
                    finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
                }
                else
                {
                    if (current.Phase == JournalPhase.Quarantined) throw new IOException("record_retirement_recovery_pending");
                    page = new(current.OperationId, current.JournalId, document.Contents[^1].Sha256, document.Contents.Length);
                }
                return operation;
            });
        if (page is not null && !CompleteCleanup(page)) throw new IOException("record_retirement_cleanup_pending");
        return selected;
    }

    private (Guid Operation, Guid Run, BlobReference Reference)? ReadResumable(Guid? after, DateTimeOffset cutoff)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        using var command = RecordRetirementRows.Command(connection, null,
            "SELECT r.operation_id,r.run_id,r.relative_path,r.size_bytes,r.sha256 FROM records.record_content_retirements r " +
            "JOIN main.operations o USING(operation_id) JOIN main.operation_journals j ON j.journal_id=r.cleanup_journal_id " +
            "JOIN main.execution_start_sources s ON s.run_id=r.run_id AND s.attempt_ordinal=1 " +
            "JOIN main.operations start ON start.operation_id=s.operation_id " +
            "WHERE o.subject_id='host:history-retention' AND r.operation_id>$0 AND julianday(start.created_at)<julianday($1) " +
            "AND (j.phase<>'committed' OR NOT EXISTS(SELECT 1 FROM main.file_effects f WHERE f.journal_id=j.journal_id AND f.state<>'notExecuted')) " +
            "AND NOT EXISTS(SELECT 1 FROM main.operations n JOIN main.operation_journals c ON c.operation_id=n.operation_id " +
            "JOIN main.file_effects f ON f.journal_id=c.journal_id WHERE n.subject_id='host:history-retention:'||r.operation_id " +
            "AND n.operation_kind='records.retention.cleanup' AND c.phase='committed' AND f.state='completed') " +
            "ORDER BY r.operation_id LIMIT 1;", after?.ToString("D") ?? "",
            cutoff.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        using var row = command.ExecuteReader();
        return row.Read() ? (Guid.ParseExact(row.GetString(0), "D"), Guid.ParseExact(row.GetString(1), "D"),
            new(row.GetString(2), row.GetInt64(3), row.GetString(4))) : null;
    }

    private RegisteredJournal? ReadResume(string subject)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = RecordRetirementRows.Command(connection, null,
            "SELECT j.journal_id FROM main.operations o JOIN main.operation_journals j USING(operation_id) " +
            "WHERE o.subject_id=$0 AND o.operation_kind='records.retention.cleanup' " +
            "ORDER BY o.created_at DESC,o.operation_id DESC LIMIT 1;", subject);
        return command.ExecuteScalar() is string id ? new RecoveryJournalRegistry(_database).ReadJournal(Guid.ParseExact(id, "D")) : null;
    }

    private static OperationIdentity ResumeIdentity(string subject, BlobReference retirement, RegisteredJournal journal) =>
        new(journal.OperationId, subject, Hash(new { retirement, cleanup = new { journal.PlanPath, journal.PlanDigest } }),
            "records.retention.cleanup");

    private ContentRetirementPage? RetirePage(Guid operationId, string subjectId, Guid runId,
        string afterSha256, int limit, RetirementContentKind kind)
    {
        if (runId == Guid.Empty || afterSha256 != "" && !RecordRetirementCodec.Digest(afterSha256) || limit is < 1 or > 128)
            throw new ArgumentException("record_retirement_query_invalid");
        var identity = RecordRetirementRows.Identity(operationId, subjectId, runId, afterSha256, limit, kind);
        _lease.VerifyHeld(_instance);
        using var root = _resources.TryAcquire(operationId, Claims).Lease ?? throw new IOException("record_retirement_admission_blocked");
        using var borrow = root.Borrow(_resources, operationId, Claims);
        return borrow.Use(() => _lease.PublishExecution<ContentRetirementPage?>(() =>
        {
            using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                var existing = OperationRows.ReadExisting(connection, transaction, identity);
                if (existing is not null)
                {
                    var saved = RecordRetirementRows.Find(connection, transaction, operationId) ?? throw RecordRetirementCodec.Invalid();
                    transaction.Commit();
                    var existingFamily = new RetirementFamilyReader(_instance, _database, _contracts).CaptureMetadata(runId);
                    return Describe(VerifiedRecordRetirement.ReadProof(_instance, _database, _contracts, existingFamily, saved));
                }
            }
            var recovery = _index.Current;
            RequireRecovery(recovery);
            var reader = new RetirementFamilyReader(_instance, _database, _contracts);
            var family = reader.CaptureMetadata(runId);
            reader.VerifyForPublication(family);
            var selected = SelectPage(reader.Candidates(family, kind), family, afterSha256, limit, kind);
            if (selected.Length == 0) return null;
            var blobs = new ImmutableBlobStore(_instance, _contracts);
            var held = new List<IDisposable>();
            try
            {
                foreach (var content in selected) held.Add(blobs.OpenVerified(content));
                var codec = new CleanupPlanCodec(_instance, _contracts, blobs);
                var cleanup = codec.Capture(operationId, Guid.NewGuid(), selected);
                var cleanupReference = codec.Persist(cleanup);
                var familyReference = RecordRetirementCodec.Write(blobs, family);
                var document = new RecordRetirementDocument(RecordRetirementCodec.Format, 1, _instance.InstanceId, operationId,
                    subjectId, runId, afterSha256, limit, familyReference, cleanup.JournalId, cleanupReference, [.. selected], kind);
                RecordRetirementCodec.Validate(document, _instance.InstanceId);
                var reference = RecordRetirementCodec.Write(blobs, document);
                foreach (var item in new[] { cleanupReference, familyReference, reference }) held.Add(blobs.OpenVerified(item));
                if (Hash(reader.CaptureMetadata(runId)) != familyReference.Sha256) throw RecordRetirementCodec.Invalid();
                if (!ReferenceEquals(recovery, _index.Current)) throw new IOException("record_retirement_recovery_changed");
                _lease.VerifyHeld(_instance);
                _database.Write((connection, transaction) =>
                {
                    _ = RecoveryJournalRegistry.ReadValidatedSnapshotInside(connection, transaction);
                    RecordRetirementRows.RequireSettledInside(connection, transaction, runId, family.Attempts.Length);
                    foreach (var attempt in family.Attempts)
                    {
                        if (AttemptReceiptStore.ReadInside(connection, transaction, attempt.Receipt.Facts.AttemptId) != attempt.Receipt)
                            throw RecordRetirementCodec.Invalid();
                        RecordRetirementRows.RequireWritersInside(connection, transaction, attempt);
                        if (kind == RetirementContentKind.Images)
                            RetirementImageEvidence.RequireCatalogInside(connection, transaction, attempt);
                        var current = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, attempt.Receipt.JournalId);
                        if (current.Phase != JournalPhase.Committed || current.Revision != attempt.Revision ||
                            current.PlanDigest != attempt.Receipt.PlanDigest) throw RecordRetirementCodec.Invalid();
                    }
                    if (!OperationRows.Accept(connection, transaction, identity, DateTimeOffset.UtcNow).IsNew) throw RecordRetirementCodec.Invalid();
                    var registered = new RegisteredJournal(cleanup.JournalId, operationId, JournalKind.FileCleanup, JournalPhase.Prepared, 1,
                        cleanupReference.RelativePath, cleanupReference.Sha256,
                        cleanup.Items.Select(item => CleanupCandidateFiles.ResourceKey(item.Blob)).Order(StringComparer.Ordinal).ToArray());
                    var effects = cleanup.Items.Select(item => new FileEffect(cleanup.JournalId, item.StepId, FileEffectAction.RemoveOwned,
                        FileEffectState.Pending, cleanupReference.Sha256, null)).ToArray();
                    RecoveryJournalRegistry.RegisterInside(connection, transaction, registered, effects, JournalWriterStore.CurrentWriter);
                    RecordRetirementRows.InsertInside(connection, transaction, document, reference);
                    _ = RecoveryJournalRegistry.ReadValidatedSnapshotInside(connection, transaction);
                    return true;
                });
                _observer?.Reached(CleanupPersistencePoint.AcceptanceCommitted, cleanup.JournalId, 1, JournalPhase.Prepared);
                var published = new RecoveryJournalRegistry(_database).ReadJournal(cleanup.JournalId);
                new FileCleanupAcceptance(_instance, _contracts, _database).MaterializeInitial(published);
                _observer?.Reached(CleanupPersistencePoint.JournalFlushed, cleanup.JournalId, 1, JournalPhase.Prepared);
                return Describe(document);
            }
            finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
        }));
    }

    private BlobReference[] SelectPage(IEnumerable<BlobReference> candidates, RecordRetirementFamily family, string after, int limit, RetirementContentKind kind)
    {
        var selected = new SortedDictionary<string, BlobReference>(StringComparer.Ordinal);
        var retirements = new RecordRetirementContents(_instance, _database, _contracts, family.RunId, family, kind);
        foreach (var item in candidates)
        {
            if (StringComparer.Ordinal.Compare(item.Sha256, after) <= 0) continue;
            if (selected.TryGetValue(item.Sha256, out var prior))
            {
                if (prior != item) throw RecordRetirementCodec.Invalid();
                continue;
            }
            if (selected.Count == limit && StringComparer.Ordinal.Compare(item.Sha256, selected.Last().Key) >= 0) continue;
            if (retirements.Read([item]).Contains(item)) continue;
            selected.Add(item.Sha256, item);
            if (selected.Count > limit) selected.Remove(selected.Last().Key);
        }
        return selected.Values.ToArray();
    }

    private static ContentRetirementPage Describe(RecordRetirementDocument document) =>
        new(document.OperationId, document.CleanupJournalId, document.Contents[^1].Sha256, document.Contents.Length);
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(value, RecordRetirementCodec.Options)));
    private static void RequireRecovery(RecoveryProjection projection)
    {
        if (!projection.IsReady || projection.GlobalBlockReason is not null || projection.Isolations.Count != 0 || projection.ResourceHolds.Count != 0)
            throw new IOException("record_retirement_recovery_pending");
    }
}
