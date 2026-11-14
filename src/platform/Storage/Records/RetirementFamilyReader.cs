using System.Runtime.Versioning;
using System.Collections.Immutable;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Records;

internal sealed class RetirementFamilyReader(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);

    internal RecordRetirementFamily CaptureMetadata(Guid runId)
    {
        var ids = new List<Guid>();
        var saved = new List<(AttemptReceipt Receipt, RegisteredJournal Journal)>();
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            using var command = RecordRetirementRows.Command(connection, transaction,
                "SELECT attempt_id FROM records.attempt_receipts WHERE run_id=$0 ORDER BY ordinal LIMIT 100;", runId);
            using (var rows = command.ExecuteReader()) while (rows.Read()) ids.Add(Guid.ParseExact(rows.GetString(0), "D"));
            if (ids.Count is < 1 or > 99) throw RecordRetirementCodec.Invalid();
            RecordRetirementRows.RequireFamilyInside(connection, transaction, runId, ids.Count);
            _ = RecoveryJournalRegistry.ReadValidatedSnapshotInside(connection, transaction);
            foreach (var id in ids)
            {
                var receipt = AttemptReceiptStore.ReadInside(connection, transaction, id) ?? throw RecordRetirementCodec.Invalid();
                saved.Add((receipt, RecoveryJournalRegistry.ReadJournalInside(connection, transaction, receipt.JournalId)));
            }
            transaction.Commit();
        }
        var attempts = ImmutableArray.CreateBuilder<RecordRetirementAttempt>();
        long metadataBytes = 4096;
        foreach (var source in saved)
        {
            var item = ReadAttempt(source.Receipt, source.Journal);
            metadataBytes = checked(metadataBytes + MetadataSize(item));
            if (metadataBytes > StrictJson.MaximumBytes) throw new IOException("record_retirement_metadata_limit");
            attempts.Add(item);
        }
        var family = new RecordRetirementFamily(RecordRetirementCodec.FamilyFormat, 1, instance.InstanceId, runId, attempts.ToImmutable());
        RecordRetirementCodec.Validate(family, instance.InstanceId);
        return family;
    }

    [SupportedOSPlatform("windows")]
    internal void VerifyForPublication(RecordRetirementFamily family)
    {
        foreach (var item in family.Attempts)
        {
            var state = AttemptWriterState.Read(instance, database, contracts, item.Receipt.JournalId);
            if (state.Journal.Phase != JournalPhase.Committed || state.Latest.Phase != JournalPhase.Committed) throw RecordRetirementCodec.Invalid();
            AttemptTerminalEvidence.RequireStopped(database, item.Receipt.JournalId);
            AttemptTerminalEvidence.RequireArtifacts(instance, database, contracts, state);
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
            using var transaction = connection.BeginTransaction(deferred: true);
            AttemptTerminalEvidence.RequireRestoredInside(connection, transaction, state,
                allowUnregistered: state.Gate.State == EffectGateState.Sealed);
            transaction.Commit();
        }
        var last = family.Attempts[^1].Receipt;
        if (!new ExecutionRunFinality(instance, database, contracts).IsFinal(last) &&
            !ExecutionRecordProjection.RequireRecoveredRecord(instance, database, contracts, last)) throw RecordRetirementCodec.Invalid();
    }

    internal IEnumerable<BlobReference> Candidates(RecordRetirementFamily family,
        RetirementContentKind kind = RetirementContentKind.Logs)
    {
        if (kind == RetirementContentKind.Images)
            return family.Attempts.SelectMany(item => item.Images).Where(item => item.Png is not null).Select(item => item.Png!);
        if (kind != RetirementContentKind.Logs) throw RecordRetirementCodec.Invalid();
        return LogCandidates(family);
    }

    private IEnumerable<BlobReference> LogCandidates(RecordRetirementFamily family)
    {
        var protectedContents = family.Attempts.SelectMany(item => item.Artifacts).SelectMany(item => item.RetainedContents)
            .Concat(family.Attempts.SelectMany(item => item.Images).Where(item => item.Png is not null).Select(item => item.Png!)).ToHashSet();
        foreach (var artifact in family.Attempts.SelectMany(item => item.Artifacts))
            foreach (var raw in artifact.RawOutputs.Select(reference => RawOutputCodec.Read(_blobs, artifact.Binding, reference)))
                foreach (var part in raw.Parts)
                {
                    if (protectedContents.Contains(part.Content)) continue;
                    yield return part.Content;
                }
    }

    private RecordRetirementAttempt ReadAttempt(AttemptReceipt receipt, RegisteredJournal registered)
    {
        if (receipt.Facts.Recovery is not (RecoveryOutcome.NotRequired or RecoveryOutcome.Restored)) throw RecordRetirementCodec.Invalid();
        var metadata = AttemptPreparedBinding.ReadPlanMetadata(instance, contracts, registered);
        var attempt = metadata.Plan; var total = metadata.Total;
        var history = new JournalStore(instance, contracts).ReadAllRevisions(receipt.JournalId);
        var journal = history[^1];
        if (registered.Phase != JournalPhase.Committed || journal.Phase != registered.Phase || journal.Revision != registered.Revision ||
            AttemptPreparedBinding.ExpectedReceipt(attempt, journal) != receipt) throw RecordRetirementCodec.Invalid();
        var artifacts = new List<RecordRetirementArtifact>();
        var contexts = new Dictionary<(StepEvidenceBinding Binding, string Role),
            (ExecutionPlan Total, StepWriterState Job, ProcessIdentity? Root)>();
        var evidenceReader = new StepEvidenceReader(instance, database, contracts);
        (ExecutionPlan Total, StepWriterState Job, ProcessIdentity? Root) Context(StepEvidenceBinding binding, string role)
        {
            if (contexts.TryGetValue((binding, role), out var context)) return context;
            if (contexts.Count == JournalCapacityLimits.MaximumWriterJobs) throw RecordRetirementCodec.Invalid();
            context = evidenceReader.ContextFromValidatedRead(binding, registered, attempt, total, journal, role);
            contexts.Add((binding, role), context); return context;
        }
        long metadataBytes = 4096;
        foreach (var result in StepResultCodec.ReadAll(journal.Root))
            AddArtifact(Main(result.Binding, result.Artifact, total, Context(result.Binding, "process"), attempt.AttemptOrdinal));
        foreach (var hook in HookResultCodec.ReadHistory(journal.Root, total))
        {
            var value = new HookEvidenceReader(instance, database, contracts).ReadFromValidatedContext(hook.Binding, hook.Role, hook.Artifact,
                Context(hook.Binding, hook.Role), verifyContents: false);
            AddArtifact(new(hook.Binding, hook.Role, hook.Artifact,
                value.RawOutput is { } raw ? [raw] : [], []));
        }
        var captures = new CaptureMetadataReader(instance, database, contracts).Read(registered, attempt, total, history);
        ImmutableArray<RecordRetirementImage> images = [];
        if (!captures.IsEmpty)
        {
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
            using var transaction = connection.BeginTransaction(deferred: true);
            var source = StartSourceRows.Read(connection, transaction, receipt.OperationId);
            StartSourceRows.RequireAttempt(source, attempt);
            var snapshot = ImageCatalogCapture.FromMetadata(receipt, journal, source, captures);
            images = RetirementImageEvidence.Read(snapshot, captures);
            transaction.Commit();
        }
        return new(receipt, checked((int)journal.Revision), Convert.ToHexStringLower(SHA256.HashData(journal.Export())), HistoryDigest(history),
            artifacts.DistinctBy(item => (item.Binding, item.Role, item.Artifact)).ToImmutableArray(), Writers(journal, total), images);

        void AddArtifact(RecordRetirementArtifact artifact)
        {
            metadataBytes = checked(metadataBytes + MetadataSize(artifact));
            if (metadataBytes > StrictJson.MaximumBytes) throw new IOException("record_retirement_metadata_limit");
            artifacts.Add(artifact);
        }
    }

    private ImmutableArray<RecordRetirementWriter> Writers(JournalDocument journal, NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan total)
    {
        var result = ImmutableArray.CreateBuilder<RecordRetirementWriter>();
        foreach (var job in WriterIntentCodec.ReadAllJobs(journal.Root, total))
        {
            var binding = StepResultCodec.Binding(journal.Root, job);
            var closure = new JobClosureStore(instance, database, contracts)
                .ReadForExecutionEvidence(binding.ParentJournalId, job.WriterId, job.JobName, job.SessionId) ?? throw RecordRetirementCodec.Invalid();
            if (job.ClosedElapsedMilliseconds != closure.RunElapsedMilliseconds) throw RecordRetirementCodec.Invalid();
            result.Add(new(binding, job.Role, new StepEvidenceReader(instance, database, contracts).ClosureReference(binding), closure.Root));
        }
        return result.ToImmutable();
    }

    private static int MetadataSize<T>(T value)
    {
        try { return BoundedJson.Serialize(value, RecordRetirementCodec.Options).Length; }
        catch (System.Text.Json.JsonException error) { throw new IOException("record_retirement_metadata_limit", error); }
    }

    private static string HistoryDigest(IReadOnlyList<JournalDocument> history)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var document in history)
        {
            var bytes = document.Export();
            digest.AppendData(BitConverter.GetBytes((long)bytes.Length)); digest.AppendData(bytes);
        }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private RecordRetirementArtifact Main(StepEvidenceBinding binding, BlobReference artifact, ExecutionPlan total,
        (ExecutionPlan Total, StepWriterState Job, ProcessIdentity? Root) context, long attemptOrdinal)
    {
        var raw = new HashSet<BlobReference>(); var retained = new HashSet<BlobReference>();
        Visit(artifact, 0);
        return new(binding, "process", artifact, raw.OrderBy(item => item.Sha256, StringComparer.Ordinal).ToImmutableArray(),
            retained.OrderBy(item => item.Sha256, StringComparer.Ordinal).ToImmutableArray());

        void Visit(BlobReference reference, int depth)
        {
            if (depth > 2) throw RecordRetirementCodec.Invalid();
            var value = StepEvidenceCodec.Read(_blobs, binding, reference);
            if (value.RawOutput is { } output) AddRaw(output);
            if (value.PreviousObservation is { } previous) Visit(previous, depth + 1);
            if (value.ProviderControl is { } provider)
                foreach (var item in new ProviderControlReader(instance, database, contracts).ReadValidatedReferences(binding, provider, context, attemptOrdinal))
                    if (item.RelativePath.StartsWith("blobs/content/", StringComparison.Ordinal)) AddRetained(item);
            if (value.Judgement is not { } judgement) return;
            var script = JudgementEvidenceCodec.Read(_blobs, binding, judgement);
            foreach (var exchange in script.Exchanges)
            {
                AddRaw(exchange.RawOutput);
                foreach (var item in JudgementInvocationArchive.Read(_blobs, exchange.Invocation).Parts) AddRetained(item);
                foreach (var probe in exchange.ProbeObservations)
                    foreach (var item in JudgementProbeArchive.Read(_blobs, probe).Parts) AddRetained(item);
            }
        }

        void AddRetained(BlobReference content)
        {
            retained.Add(content);
            if (retained.Count > 32768) throw new IOException("record_retirement_metadata_limit");
        }

        void AddRaw(BlobReference reference)
        {
            if (!raw.Add(reference)) return;
            if (raw.Count > 8192) throw new IOException("record_retirement_metadata_limit");
            var manifest = RawOutputCodec.Read(_blobs, binding, reference);
            if (manifest.FileLogBaseline is not { } baseline) return;
            foreach (var item in LogBaselineCodec.Read(_blobs, baseline, binding.ParentJournalId, binding.StepId, binding.JobName,
                total.Steps.Single(step => step.StepId == binding.StepId).LogTargets).Candidates)
                if (item.Checkpoint is { } checkpoint) AddRetained(checkpoint);
        }
    }
}
