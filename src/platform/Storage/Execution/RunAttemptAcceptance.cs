using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Scheduling;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed record AttemptAcceptanceResult(OperationAcceptance Acceptance, Guid JournalId, BlobReference Plan) : IExecutionAdmissionReceipt;

public sealed class RunAttemptAcceptance(RuntimeInstance instance, ContractReader contracts, StateDatabase database, IAttemptPersistenceObserver? observer = null)
{
    public AttemptAcceptanceResult Accept(OperationIdentity operation, RunAttemptPlan candidate, DateTimeOffset acceptedAt)
    {
        RejectDirectStart(operation);
        if (database.InstanceId != instance.InstanceId || operation.OperationId.ToString("D") != candidate.OperationId)
            throw new IOException("attempt_acceptance_identity_unbound");
        var replay = Lookup(operation, candidate);
        if (replay is not null) return replay;
        var codec = new AttemptPlanCodec(instance, contracts, new(instance, contracts));
        using var prepared = codec.Freeze(candidate);
        return AcceptPrepared(operation, prepared, acceptedAt, null);
    }

    internal AttemptAcceptanceResult AcceptStart(OperationIdentity operation, ExecutionPlanDraft draft,
        PreparedAttemptPlan prepared, DateTimeOffset acceptedAt, PreparedExecutionOrigin? scheduledOrigin = null, PreparedExecutionRetry? retry = null)
    {
        var request = draft.Explanation.Selection.Request;
        if (operation != ExecutionStartIdentity.Create(operation.OperationId, operation.SubjectId, request))
            throw new OperationConflictException();
        var replay = LookupStart(operation);
        if (replay is not null) return replay;
        var intended = draft.ReadPlan() ?? throw new IOException("execution_start_plan_unbound");
        var candidate = prepared.Plan.ReadWire();
        if (candidate.ExecutionPlan.Sha256 != draft.PlanDigest || candidate.ExecutionPlan.RelativePath != $"blobs/plans/{draft.PlanDigest}.json" ||
            candidate.ExecutionPlan.SizeBytes != intended.Export().LongLength)
            throw new IOException("execution_start_plan_unbound");
        return AcceptPrepared(operation, prepared, acceptedAt, request, scheduledOrigin, retry);
    }

    private AttemptAcceptanceResult AcceptPrepared(OperationIdentity operation, PreparedAttemptPlan prepared,
        DateTimeOffset acceptedAt, ExecutionSelectionRequest? request, PreparedExecutionOrigin? scheduledOrigin = null, PreparedExecutionRetry? retry = null)
    {
        ExecutionRetryIntent? retryIntent = null;
        if (retry is not null)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            retryIntent = retry.Intent;
        }
        var plan = prepared.Plan.ReadWire();
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            operation.OperationId.ToString("D") != plan.OperationId) throw new IOException("attempt_acceptance_identity_unbound");
        var journal = new RegisteredJournal(Guid.Parse(plan.JournalId), operation.OperationId, JournalKind.RunAttempt, JournalPhase.Prepared, 1,
            prepared.Reference.RelativePath, prepared.Reference.Sha256, Array.AsReadOnly(plan.ResourceKeys.ToArray()));
        var total = new ExecutionPlanCodec(instance, contracts, new(instance, contracts)).Read(AttemptPlanCodec.Reference(plan.ExecutionPlan));
        var capacity = ExecutionRecoveryCapacity.Measure(total.ReadWire());
        if (request is not null && total.ReadWire().SettingsRevision != request.ExpectedSettingsRevision)
            throw new ExecutionSelectionException("execution_settings_revision_conflict");
        var configurations = request is null ? null : ExecutionConfigurationCursors.Read(instance, contracts, plan, total.ReadWire());
        if (retry is not null)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            retry.RequireConfigurations(configurations ?? throw new IOException("configuration_retry_source_unbound"));
        }
        var claims = ExecutionPlanResources.Claims(total.ReadWire());
        var writer = JournalWriterStore.CurrentWriter;
        var result = database.Write((connection, transaction) =>
        {
            if (request is not null && OperationRows.ReadExisting(connection, transaction, operation) is null)
            {
                ExecutionRetryRows.RequireExplicit(connection, transaction, operation.OperationId, retryIntent);
                PreparedExecutionOrigin.RequireExplicitOrigin(connection, transaction, operation.OperationId, scheduledOrigin);
                if (retry is not null)
                {
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                    retry.RequireInside(connection, transaction, operation, request, plan, total.ReadWire());
                }
                else
                {
                    scheduledOrigin?.RequireCurrentInside(connection, transaction, operation, request, plan, total.ReadWire());
                    RequireSourceCursors(connection, transaction, request);
                    configurations!.RequireCurrent(connection, transaction);
                    JudgementNetworkBinding.RequireCurrent(connection, transaction, total.ReadWire());
                    ExecutionRetryAuthority.RequireFrozenSettings(connection, transaction, total.ReadWire());
                }
            }
            var receipt = RecoveryJournalRegistry.AcceptPreparedInside(connection, transaction, operation, journal, [], acceptedAt, claims, writer, capacity);
            if (request is not null && receipt.Acceptance.IsNew)
                StartSourceRows.Insert(connection, transaction, operation, request, plan, retryIntent?.PreviousOperationId);
            return receipt;
        });
        if (!result.Acceptance.IsNew)
        {
            var original = new RecoveryJournalRegistry(database).ReadJournal(result.JournalId);
            if (request is null && original.PlanDigest != prepared.Reference.Sha256) throw new OperationConflictException();
            var reference = AttemptPreparedBinding.Reference(instance, original);
            JournalResourceClaims.VerifyAttempt(instance, database, contracts, original);
            return new(result.Acceptance, original.JournalId, reference);
        }
        observer?.Reached(AttemptPersistencePoint.AcceptanceCommitted, journal.JournalId, 1, JournalPhase.Prepared);
        MaterializeInitial(journal, plan);
        return new(result.Acceptance, journal.JournalId, prepared.Reference);
    }

    internal AttemptAcceptanceResult? LookupStart(OperationIdentity operation)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot || operation.Kind != "execution.start")
            throw new IOException("attempt_acceptance_identity_unbound");
        OperationAcceptance accepted;
        RegisteredJournal journal;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            var existing = OperationRows.ReadExisting(connection, transaction, operation);
            if (existing is null) return null;
            accepted = existing;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT initial_journal_id FROM main.operations WHERE operation_id=$operation;";
            command.Parameters.AddWithValue("$operation", operation.OperationId.ToString("D"));
            var id = command.ExecuteScalar() as string ?? throw new IOException("accepted_operation_requires_reconciliation");
            journal = RecoveryJournalRegistry.ReadInside(connection, transaction, Guid.Parse(id));
            if (journal.OperationId != operation.OperationId || journal.Kind != JournalKind.RunAttempt)
                throw new IOException("attempt_acceptance_identity_unbound");
            var source = StartSourceRows.Read(connection, transaction, operation.OperationId);
            if (source.JournalId != journal.JournalId || source.Identity != operation) throw new OperationConflictException();
            StartSourceRows.RequireAttempt(source, new AttemptPlanCodec(instance, contracts, new(instance, contracts))
                .Read(AttemptPreparedBinding.Reference(instance, journal)).ReadWire());
            transaction.Commit();
        }
        JournalResourceClaims.VerifyAttempt(instance, database, contracts, journal);
        return new(accepted, journal.JournalId, AttemptPreparedBinding.Reference(instance, journal));
    }

    private static void RequireSourceCursors(SqliteConnection connection, SqliteTransaction transaction, ExecutionSelectionRequest request)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM automation.automation_catalog_state WHERE singleton=1;";
        if ((long?)command.ExecuteScalar() != request.ExpectedCatalogRevision)
            throw new ExecutionSelectionException("execution_catalog_revision_conflict");
        command.CommandText = "SELECT revision FROM system.settings_current WHERE singleton=1;";
        if ((long?)command.ExecuteScalar() != request.ExpectedSettingsRevision)
            throw new ExecutionSelectionException("execution_settings_revision_conflict");
    }

    public AttemptAcceptanceResult? Lookup(OperationIdentity operation, RunAttemptPlan candidate)
    {
        RejectDirectStart(operation);
        if (database.InstanceId != instance.InstanceId || operation.OperationId.ToString("D") != candidate.OperationId)
            throw new IOException("attempt_acceptance_identity_unbound");
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = connection.BeginTransaction(deferred: true);
        var accepted = OperationRows.ReadExisting(connection, transaction, operation);
        if (accepted is null) return null;
        var journal = new RecoveryJournalRegistry(database).ReadOperationJournal(operation.OperationId);
        if (Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(candidate))) != journal.PlanDigest)
            throw new OperationConflictException();
        JournalResourceClaims.VerifyAttempt(instance, database, contracts, journal);
        return new(accepted, journal.JournalId, AttemptPreparedBinding.Reference(instance, journal));
    }

    private static void RejectDirectStart(OperationIdentity operation)
    {
        if (operation.Kind == "execution.start") throw new IOException("execution_start_requires_opaque_draft");
    }

    internal void MaterializeInitial(RegisteredJournal journal, RunAttemptPlan plan)
    {
        var document = ExecutionInitialJournal.Create(instance.InstanceId, journal, plan.RunId, plan.AttemptId);
        new JournalStore(instance, contracts).Create(JsonSerializer.SerializeToUtf8Bytes(document));
        observer?.Reached(AttemptPersistencePoint.JournalCreated, journal.JournalId, 1, JournalPhase.Prepared);
    }
}
