using System.Globalization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class CommittedAttemptJournal
{
    internal static JournalDocument Read(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        RegisteredJournal registered, RunAttemptPlan attempt)
    {
        if (registered is not { Kind: JournalKind.RunAttempt, Phase: JournalPhase.Committed } ||
            registered.Revision is < 1 or > JournalCapacityLimits.MaximumRevisions) throw Invalid();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        using var transaction = connection.BeginTransaction(deferred: true);
        var current = RecoveryJournalRegistry.ReadInside(connection, transaction, registered.JournalId);
        AttemptPreparedBinding.RequireRegistration(current, attempt);
        var receipt = AttemptReceiptStore.ReadInside(connection, transaction, Guid.Parse(attempt.AttemptId)) ?? throw Invalid();
        using var operation = DatabaseConnections.Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM main.operations WHERE operation_id=$0 AND initial_journal_id=$1 AND status='completed');",
            current.OperationId.ToString("D"), current.JournalId.ToString("D"));
        if (current.Phase != registered.Phase || current.Revision != registered.Revision ||
            current.PlanPath != registered.PlanPath || current.PlanDigest != registered.PlanDigest ||
            receipt.JournalId != current.JournalId || receipt.OperationId != current.OperationId ||
            receipt.PlanDigest != current.PlanDigest || (long)operation.ExecuteScalar()! != 1) throw Invalid();
        using var publication = JournalPublicationGate.Acquire(instance, current.JournalId);
        var directory = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "journals", current.JournalId.ToString("D")));
        using var held = StableDirectoryLease.Acquire(directory);
        var names = Directory.EnumerateFileSystemEntries(directory).Take(JournalCapacityLimits.MaximumRevisions + 1)
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        if (names.Length != current.Revision || names.Where((name, index) => name != Name(index + 1)).Any()) throw Invalid();
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(directory, Name(current.Revision)));
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > StrictJson.MaximumBytes) throw Invalid();
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var document = JournalDocument.Read(bytes, contracts);
        if (document.InstanceId != instance.InstanceId || document.JournalId != current.JournalId ||
            document.Kind != current.Kind || document.Phase != current.Phase || document.Revision != current.Revision ||
            document.PlanPath != current.PlanPath || document.PlanDigest != current.PlanDigest ||
            !document.Root.GetProperty("resourceKeys").EnumerateArray().Select(item => item.GetString()).SequenceEqual(current.ResourceKeys) ||
            AttemptPreparedBinding.ExpectedReceipt(attempt, document) != receipt) throw Invalid();
        // The receipt atomically anchors the complete append-only terminal document; prefix replay belongs to temporal consumers.
        transaction.Commit();
        return document;
    }

    private static string Name(long revision) => revision.ToString("D10", CultureInfo.InvariantCulture) + ".json";
    private static IOException Invalid() => new("execution_committed_reference_unbound");
}
