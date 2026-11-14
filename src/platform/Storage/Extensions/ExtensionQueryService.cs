using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Operations;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Globalization;

namespace NexusPipeline.Platform.Storage.Extensions;

public sealed class ExtensionQueryService : IExtensionOperationQueries
{
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _lease;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly PayloadApplyRoots _roots;
    private readonly OperationQueryService _operations;

    public ExtensionQueryService(RuntimeInstance instance, InstanceLease lease, StateDatabase database,
        ContractReader contracts, PayloadApplyRoots roots)
    {
        _operations = new(instance, lease, database);
        _instance = instance; _lease = lease; _database = database; _contracts = contracts; _roots = roots;
    }

    public ExtensionApplyState? Read(Guid operationId, string subject) => _lease.PublishExecution(() => ReadCore(operationId, subject));

    public ExtensionOperationPage List(string subject, int offset, int limit) => _lease.PublishExecution<ExtensionOperationPage>(() =>
    {
        if (subject is not { Length: >= 1 and <= 256 } || subject.Any(char.IsControl) ||
            offset is < 0 or > 10000 || limit is < 1 or > 50) throw new ArgumentException("extension_operation_page_invalid");
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Extensions | DatabaseDomain.System);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT operation_id,created_at FROM main.operations WHERE subject_id=$subject AND operation_kind='extension.apply' " +
            "ORDER BY created_at DESC,operation_id DESC LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$limit", limit + 1);
        command.Parameters.AddWithValue("$offset", offset);
        var rows = new List<(Guid Id, DateTimeOffset Created)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) rows.Add((Guid.ParseExact(reader.GetString(0), "D"),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        transaction.Commit();
        var items = rows.Take(limit).Select(row =>
        {
            var state = ReadCore(row.Id, subject) ?? throw Invalid();
            return new ExtensionOperationItem(row.Id, row.Created, state.ExtensionId, state.Action,
                state.Operation.Status, state.Outcome);
        }).ToArray();
        return new(items, rows.Count > limit && offset + limit <= 10000 ? offset + limit : null);
    });

    private ExtensionApplyState? ReadCore(Guid operationId, string subject)
    {
        var operation = _operations.Read(operationId, subject);
        if (operation is null || operation.Kind != "extension.apply") return null;
        if (operation.InitialJournalId is not { } journalId) throw Invalid();
        var registered = new RecoveryJournalRegistry(_database).ReadJournal(journalId);
        if (registered.OperationId != operationId || registered.Kind != JournalKind.ExtensionApply ||
            operation.Status != (registered.Phase == JournalPhase.Committed ? "completed" : "accepted")) throw Invalid();
        var gate = new JournalEffectGate(_database).Read(journalId);
        if (gate.State != EffectGateState.Entered)
        {
            var plan = PayloadAcceptanceLookup.ReadUnenteredPlan(_instance, _database, _contracts, _roots, registered, gate);
            var source = new PayloadSourceCodec(_instance, _contracts).ReadMetadata(plan.ExtensionSource ?? throw Invalid());
            var accepted = new PayloadAcceptanceLookup(_instance, _database, _contracts, _roots)
                .ReadAccepted(operationId, subject, ExtensionApplyRequest.FromSource(source)) ?? throw Invalid();
            if (accepted.JournalId != journalId) throw Invalid();
            return new(operation, source.ExtensionId, registered.Phase,
                registered.Phase == JournalPhase.Committed ? ExtensionApplyOutcome.NotExecuted : ExtensionApplyOutcome.Pending,
                null, null, null) { Action = Action(source.Action) };
        }
        var bound = new ExtensionPayloadBinding(_instance, _database, _contracts, _roots).Read(journalId);
        if (bound.Identity.OperationId != operationId || bound.Identity.SubjectId != subject) throw Invalid();
        if (registered.Phase != JournalPhase.Committed)
            return new(operation, bound.Source.ExtensionId, registered.Phase, ExtensionApplyOutcome.Pending, null, null, null)
                { Action = Action(bound.Source.Action) };
        if (!bound.Restoration.IsEmpty)
        {
            var revision = bound.Source.ExpectedInstalledRevision;
            var installed = false;
            if (revision != 0)
            {
                using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Extensions | DatabaseDomain.System);
                using var transaction = connection.BeginTransaction(deferred: true);
                installed = ExtensionInstalledRows.ReadMetadataAt(connection, transaction, bound.Source.ExtensionId, revision).IsInstalled;
                transaction.Commit();
            }
            return new(operation, bound.Source.ExtensionId, registered.Phase, ExtensionApplyOutcome.OriginalRestored,
                revision, installed, null) { Action = Action(bound.Source.Action) };
        }
        var commit = bound.Commit ?? throw Invalid(); var footprint = bound.Footprint ?? throw Invalid();
        return new(operation, bound.Source.ExtensionId, registered.Phase,
            footprint.IsInstalled ? ExtensionApplyOutcome.Installed : ExtensionApplyOutcome.Removed,
            commit.InstalledRevision, footprint.IsInstalled, commit.ActivationId)
            { Action = Action(bound.Source.Action) };
    }

    private static IOException Invalid() => new("extension_operation_query_unbound");
    private static ExtensionApplyAction Action(PayloadAction source) => source switch
    {
        PayloadAction.Install => ExtensionApplyAction.Install,
        PayloadAction.Upgrade => ExtensionApplyAction.Upgrade,
        PayloadAction.Remove => ExtensionApplyAction.Remove,
        _ => throw Invalid()
    };
}
