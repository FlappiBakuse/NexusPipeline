using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Notifications;

namespace NexusPipeline.Platform.Storage.Secrets;

internal sealed class SecretFieldRetirement : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly Guid _secretId;
    private readonly Guid _operationId;
    private readonly FrozenFileCleanup? _plan;
    private readonly BlobReference? _reference;
    private readonly IDisposable? _held;
    private readonly JournalWriter _writer;

    private SecretFieldRetirement(RuntimeInstance instance, Guid id, Guid operationId, FrozenFileCleanup? plan,
        BlobReference? reference, IDisposable? held)
    {
        _instance = instance; _secretId = id; _operationId = operationId;
        _plan = plan; _reference = reference; _held = held; _writer = JournalWriterStore.CurrentWriter;
    }

    internal static SecretFieldRetirement Prepare(RuntimeInstance instance, StateDatabase database, Guid operationId, Guid id)
    {
        BlobReference candidate;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT digest,size_bytes,state FROM secret_payloads WHERE secret_id=$id;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            using var row = command.ExecuteReader();
            if (!row.Read() || row.GetString(2) != "committed") throw new IOException("secret_retirement_owner_unverified");
            candidate = new($"secrets/payloads/{id:D}-{row.GetString(0)}.bin", row.GetInt64(1), row.GetString(0));
        }
        CleanupCandidateFiles.Validate(candidate);
        var blobs = new ImmutableBlobStore(instance, new());
        FrozenFileCleanup plan;
        try { plan = new CleanupPlanCodec(instance, new(), blobs).Capture(operationId, Guid.NewGuid(), [candidate]); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Unknown bytes retain an explicit retirement fact, without receiving a deletion plan.
            return new(instance, id, operationId, null, null, null);
        }
        var reference = new CleanupPlanCodec(instance, new(), blobs).Persist(plan);
        return new(instance, id, operationId, plan, reference, blobs.OpenVerified(reference));
    }

    internal void RegisterInside(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (_plan is not null && _reference is not null)
        {
            var journal = new RegisteredJournal(_plan.JournalId, _operationId, JournalKind.FileCleanup, JournalPhase.Prepared, 1,
                _reference.RelativePath, _reference.Sha256, _plan.Items.Select(value => CleanupCandidateFiles.ResourceKey(value.Blob)).ToArray());
            var effects = _plan.Items.Select(value => new FileEffect(journal.JournalId, value.StepId, FileEffectAction.RemoveOwned,
                FileEffectState.Pending, _reference.Sha256, null)).ToArray();
            RecoveryJournalRegistry.RegisterInside(connection, transaction, journal, effects, _writer);
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE secret_payloads SET state='retired' WHERE secret_id=$id AND state='committed' " +
                "AND NOT EXISTS(SELECT 1 FROM secret_fields WHERE secret_id=$id);";
            command.Parameters.AddWithValue("$id", _secretId.ToString("D"));
            if (command.ExecuteNonQuery() != 1) throw new IOException("secret_retirement_reference_changed");
            command.CommandText = "INSERT INTO secret_retirements VALUES($id,$operation,$journal,$state);";
            command.Parameters.AddWithValue("$operation", _operationId.ToString("D"));
            command.Parameters.AddWithValue("$journal", _plan is null ? DBNull.Value : _plan.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$state", _plan is null ? "unverified" : "planned");
            command.ExecuteNonQuery();
        }
    }

    internal void Materialize(StateDatabase database)
    {
        if (_plan is null) return;
        var registered = new RecoveryJournalRegistry(database).ReadJournal(_plan.JournalId);
        new FileCleanupAcceptance(_instance, new(), database).MaterializeInitial(registered);
    }

    internal SecretPreparationDefsRetirement Describe() => new()
    {
        State = _plan is null ? "unverified" : "planned", JournalId = _plan?.JournalId.ToString("D"),
        Plan = _reference is null ? null : new() { RelativePath = _reference.RelativePath, SizeBytes = _reference.SizeBytes, Sha256 = _reference.Sha256 }
    };

    internal NotificationSecretRetirement DescribeNotification()
    {
        var description = Describe();
        return new(description.State, description.JournalId is null ? null : Guid.Parse(description.JournalId),
            description.Plan is null ? null : new(description.Plan.RelativePath, description.Plan.SizeBytes, description.Plan.Sha256));
    }

    internal static SecretFieldRetirement? FromNotification(RuntimeInstance instance, Guid operationId, Guid? before,
        NotificationSecretRetirement? description)
    {
        if (before is null)
        {
            if (description is not null) throw new IOException("secret_retirement_unexpected");
            return null;
        }
        if (description is null) throw new IOException("secret_retirement_missing");
        return FromDescription(instance, operationId, before.Value, new()
        {
            State = description.State, JournalId = description.JournalId?.ToString("D"),
            Plan = description.Plan is not { } reference ? null :
                new() { RelativePath = reference.RelativePath, SizeBytes = reference.SizeBytes, Sha256 = reference.Sha256 }
        });
    }

    internal static SecretFieldRetirement? FromFrozen(RuntimeInstance instance, SecretPreparationPlan plan) =>
        plan.BeforeSecretId is null ? null : FromDescription(instance, Guid.Parse(plan.OperationId), Guid.Parse(plan.BeforeSecretId), plan.Retirement);

    internal static SecretFieldRetirement FromDescription(RuntimeInstance instance, Guid operationId, Guid before, SecretPreparationDefsRetirement description)
    {
        if (description.State == "unverified") return new(instance, before, operationId, null, null, null);
        if (description.State != "planned") throw new IOException("secret_retirement_frozen_scope_unbound");
        var wire = description.Plan ?? throw new IOException("secret_retirement_plan_missing");
        var reference = new BlobReference(wire.RelativePath, wire.SizeBytes, wire.Sha256);
        var blobs = new ImmutableBlobStore(instance, new());
        var frozen = new CleanupPlanCodec(instance, new(), blobs).Read(reference);
        if (frozen.OperationId != operationId || frozen.JournalId.ToString("D") != description.JournalId ||
            frozen.Items.Count != 1 || CleanupCandidateFiles.SecretId(frozen.Items[0].Blob) != before)
            throw new IOException("secret_retirement_frozen_scope_unbound");
        return new(instance, before, operationId, frozen, reference, blobs.OpenVerified(reference));
    }

    public void Dispose() => _held?.Dispose();
}
