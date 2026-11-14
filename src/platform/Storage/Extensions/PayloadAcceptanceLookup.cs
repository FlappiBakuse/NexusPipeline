using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class PayloadAcceptanceLookup(RuntimeInstance instance, StateDatabase database,
    ContractReader contracts, PayloadApplyRoots roots)
{
    internal PayloadAcceptanceResult? ReadAccepted(Guid operationId, string subject, ExtensionApplyRequest request)
    {
        if (database.InstanceId != instance.InstanceId ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.RuntimeRoot, instance.RuntimeRoot))
            throw Invalid();
        var identity = request.Identity(operationId, subject);
        OperationAcceptance acceptance; RegisteredJournal registered;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Extensions | DatabaseDomain.System))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            var existing = OperationRows.ReadExisting(connection, transaction, identity);
            if (existing is null) return null;
            acceptance = existing; registered = ReadInitial(connection, transaction, operationId);
            transaction.Commit();
        }
        var gate = new JournalEffectGate(database).Read(registered.JournalId);
        var plan = gate.State == EffectGateState.Entered
            ? new ExtensionPayloadBinding(instance, database, contracts, roots).Read(registered.JournalId).Plan
            : ReadUnenteredPlan(instance, database, contracts, roots, registered, gate);
        if (plan.Kind != JournalKind.ExtensionApply || plan.OperationId != operationId ||
            plan.JournalId != registered.JournalId || plan.ExtensionSource is null) throw Invalid();
        var codec = new PayloadSourceCodec(instance, contracts);
        var source = codec.ReadMetadata(plan.ExtensionSource);
        codec.RequireFrozenBinding(source, plan);
        if (ExtensionApplyRequest.FromSource(source).Identity(operationId, subject) != identity) throw new OperationConflictException();
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Extensions | DatabaseDomain.System))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            if (OperationRows.ReadExisting(connection, transaction, identity) != acceptance) throw Invalid();
            var fresh = ReadInitial(connection, transaction, operationId);
            if (fresh.OperationId != registered.OperationId || fresh.JournalId != registered.JournalId ||
                fresh.Kind != registered.Kind || fresh.Phase != registered.Phase || fresh.Revision != registered.Revision ||
                fresh.PlanDigest != registered.PlanDigest || fresh.PlanPath != registered.PlanPath ||
                !fresh.ResourceKeys.SequenceEqual(registered.ResourceKeys)) throw Invalid();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT resource_key,mode FROM main.journal_resource_claims WHERE journal_id=$id ORDER BY resource_key LIMIT 4097;";
            command.Parameters.AddWithValue("$id", registered.JournalId.ToString("D"));
            using (var row = command.ExecuteReader())
            {
                var index = 0;
                while (row.Read())
                {
                    if (index >= source.Claims.Length || row.GetString(0) != source.Claims[index].ResourceKey ||
                        row.GetInt32(1) != (int)source.Claims[index].Mode) throw Invalid();
                    index++;
                }
                if (index != source.Claims.Length) throw Invalid();
            }
            transaction.Commit();
        }
        var full = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, registered.PlanPath));
        var reference = new BlobReference(registered.PlanPath, new FileInfo(full).Length, registered.PlanDigest);
        new ImmutableBlobStore(instance, contracts).Verify(reference);
        return new(acceptance, registered.JournalId, reference);
    }

    internal static FrozenPayloadApply ReadUnenteredPlan(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, PayloadApplyRoots roots, RegisteredJournal registered, EffectGateSnapshot gate)
    {
        var directory = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot,
            "journals", registered.JournalId.ToString("D")));
        if (Path.Exists(directory))
            return PayloadPreparedBinding.Read(instance, contracts, database, roots, registered.JournalId).Plan;
        if (registered.Kind != JournalKind.ExtensionApply || registered.Phase != JournalPhase.Prepared ||
            registered.Revision != 1 || gate.State != EffectGateState.Unentered || gate.Revision != 1 ||
            gate.InstanceId != instance.InstanceId || gate.JournalId != registered.JournalId ||
            gate.OperationId != registered.OperationId || gate.Kind != registered.Kind || gate.PlanDigest != registered.PlanDigest)
            throw Invalid();
        var plan = PayloadPreparedBinding.ReadPlan(instance, contracts, roots, registered);
        var snapshot = new RecoveryJournalRegistry(database).ReadSnapshot(registered.JournalId);
        RecoveryRegistryPolicy.Verify(snapshot);
        PayloadPreparedBinding.VerifyPending(snapshot, plan, registered);
        return plan;
    }

    private static RegisteredJournal ReadInitial(SqliteConnection connection, SqliteTransaction transaction, Guid operationId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT initial_journal_id FROM main.operations WHERE operation_id=$id AND operation_kind='extension.apply';";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        var value = command.ExecuteScalar();
        if (value is not string text || !Guid.TryParseExact(text, "D", out var journalId) || journalId == Guid.Empty) throw Invalid();
        var registered = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, journalId);
        if (registered.Kind != JournalKind.ExtensionApply || registered.OperationId != operationId) throw Invalid();
        return registered;
    }

    private static IOException Invalid() => new("extension_apply_acceptance_unbound");
}
