using System.Runtime.Versioning;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Platform.Storage.Scheduling;

[SupportedOSPlatform("windows")]
internal static class WorkEffectInventory
{
    internal static void Verify(RuntimeInstance instance, StateDatabase database, ContractReader contracts, bool rotations = true)
    {
        var origins = new Dictionary<Guid, WorkEffectOrigin>();
        ManualWorkInventory.Visit(instance, database, contracts, state =>
        {
            var source = state.Source.Document;
            origins.Add(source.OperationId, new(source.ExtensionId, source.ProviderId, source.Task.TaskId,
                source.Invocation.RunId, source.Invocation.AttemptId, source.LocalDate, state.Begin is not null));
        });
        ScheduledProviderInventory.Visit(instance, database, contracts, state =>
        {
            var source = state.Source.Document;
            origins.Add(source.OperationId, new(source.ExtensionId, source.ProviderId, source.ManualTask?.TaskId ?? source.Task.TaskId,
                source.Invocation.RunId, source.Invocation.AttemptId, source.HostLocalDate, state.Begin is not null));
        });
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        using var transaction = DatabaseConnections.BeginRead(connection);
        using (var versions = connection.CreateCommand())
        {
            versions.Transaction = transaction; versions.CommandText = "SELECT extension_id,field,identity_version FROM main.work_identity_versions;";
            using var rows = versions.ExecuteReader();
            while (rows.Read())
                if (rows.GetString(0).Length == 0 || rows.GetString(1).Length == 0 || rows.GetString(2) is not { Length: >= 1 and <= 32 } version ||
                    version.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-'))) throw WorkEffectRows.Invalid();
        }
        using var ids = connection.CreateCommand(); ids.Transaction = transaction; ids.CommandText = "SELECT claim_id FROM main.work_effect_claims ORDER BY claim_id;";
        var claims = new List<Guid>();
        using (var rows = ids.ExecuteReader())
            while (rows.Read()) { if (claims.Count >= 100000) throw WorkEffectRows.Invalid(); claims.Add(Guid.ParseExact(rows.GetString(0), "D")); }
        foreach (var id in claims)
        {
            var claim = WorkEffectRows.Require(connection, transaction, id); var owner = claim.Owner;
            var source = Require(origins, owner.OperationId, owner.RunId, owner.AttemptId, claim.Key);
            if (source.ProviderId != owner.ProviderId || source.TaskId != owner.TaskId || owner.SecretRevision < 1) throw WorkEffectRows.Invalid();
            if (claim.ParentId is { } parentId)
            {
                var parent = WorkEffectRows.Require(connection, transaction, parentId);
                if (parent.ParentId is not null || parent.Key != claim.Key || claim.RoleId.Length == 0) throw WorkEffectRows.Invalid();
            }
            else if (claim.RoleId.Length != 0 || claim.ServerId.Length != 0) throw WorkEffectRows.Invalid();
            using (var pin = connection.CreateCommand())
            {
                pin.Transaction = transaction; pin.CommandText = "SELECT COUNT(*) FROM main.work_identity_versions WHERE extension_id=$ext AND field=$field;";
                pin.Parameters.AddWithValue("$ext", claim.Key.ExtensionId); pin.Parameters.AddWithValue("$field", claim.Key.Field);
                if ((long)pin.ExecuteScalar()! != 1) throw WorkEffectRows.Invalid();
            }
            var state = WorkEffectRows.State(connection, transaction, id);
            var closed = EffectEpochInventory.Verify(connection, transaction, claim, origins);
            if (closed && claim.ParentId is null)
            {
                var children = WorkEffectRows.Children(connection, transaction, id);
                if (children.Count == 0 || children.Any(child => WorkEffectRows.State(connection, transaction, child).Kind != "confirmed"))
                    throw WorkEffectRows.Invalid();
            }
            using var events = connection.CreateCommand(); events.Transaction = transaction;
            events.CommandText = "SELECT operation_id,run_id,attempt_id,kind FROM main.work_effect_events WHERE claim_id=$id ORDER BY sequence;";
            events.Parameters.AddWithValue("$id", id.ToString("D")); using var facts = events.ExecuteReader();
            while (facts.Read())
            {
                var operation = Guid.ParseExact(facts.GetString(0), "D");
                _ = Require(origins, operation, Guid.ParseExact(facts.GetString(1), "D"), Guid.ParseExact(facts.GetString(2), "D"), claim.Key);
                if (facts.GetString(3) is "acquired" or "submitted" && operation != owner.OperationId) throw WorkEffectRows.Invalid();
                if (facts.GetString(3) == "submitted" && claim.ParentId is null) throw WorkEffectRows.Invalid();
            }
        }
        if (rotations) EffectRotationInventory.Verify(instance, database, contracts, origins);
    }

    internal static WorkEffectOrigin Require(IReadOnlyDictionary<Guid, WorkEffectOrigin> origins, Guid operation, Guid run, Guid attempt, WorkEffectKey key)
    {
        if (!origins.TryGetValue(operation, out var source) || !source.Begun || source.ExtensionId != key.ExtensionId ||
            source.RunId != run || source.AttemptId != attempt || source.LocalDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) != key.LocalDate)
            throw WorkEffectRows.Invalid();
        return source;
    }
}

internal sealed record WorkEffectOrigin(string ExtensionId, string ProviderId, Guid TaskId, Guid RunId, Guid AttemptId, DateOnly LocalDate, bool Begun);

[SupportedOSPlatform("windows")]
internal static class EffectEpochInventory
{
    internal static bool Verify(SqliteConnection connection, SqliteTransaction transaction, WorkEffectClaim claim,
        IReadOnlyDictionary<Guid, WorkEffectOrigin> origins)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT e.ordinal,e.operation_id,e.provider_id,e.task_id,e.run_id,e.attempt_id,e.secret_id,e.secret_revision,r.operation_id,r.run_id,r.attempt_id " +
            "FROM main.work_effect_epochs e LEFT JOIN main.work_effect_epoch_results r ON r.claim_id=e.claim_id AND r.ordinal=e.ordinal WHERE e.claim_id=$id ORDER BY e.ordinal;";
        command.Parameters.AddWithValue("$id", claim.Id.ToString("D"));
        using var rows = command.ExecuteReader(); long ordinal = 0; var closed = true;
        while (rows.Read())
        {
            if (claim.ParentId is not null || !closed || rows.GetInt64(0) != ++ordinal || ordinal > 4096 || rows.GetInt64(7) < 1)
                throw WorkEffectRows.Invalid();
            var owner = new WorkEffectOwner(Id(rows.GetString(1)), rows.GetString(2), Id(rows.GetString(3)), Id(rows.GetString(4)),
                Id(rows.GetString(5)), Id(rows.GetString(6)), rows.GetInt64(7));
            var source = WorkEffectInventory.Require(origins, owner.OperationId, owner.RunId, owner.AttemptId, claim.Key);
            if (owner.ProviderId != source.ProviderId || owner.TaskId != source.TaskId || ordinal == 1 && owner != claim.Owner)
                throw WorkEffectRows.Invalid();
            closed = !rows.IsDBNull(8);
            if (closed)
                _ = WorkEffectInventory.Require(origins, Id(rows.GetString(8)), Id(rows.GetString(9)), Id(rows.GetString(10)), claim.Key);
        }
        if (claim.ParentId is null && ordinal == 0) throw WorkEffectRows.Invalid();
        if (claim.ParentId is not null) return false;
        if (closed && WorkEffectRows.State(connection, transaction, claim.Id).Kind != "confirmed") throw WorkEffectRows.Invalid();
        return closed;
    }

    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw WorkEffectRows.Invalid();
}

[SupportedOSPlatform("windows")]
internal static class EffectRotationInventory
{
    internal static void Verify(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        IReadOnlyDictionary<Guid, WorkEffectOrigin> origins)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        using var transaction = DatabaseConnections.BeginRead(connection);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT claim_id,operation_id,source_operation_id,run_id,attempt_id,before_secret_id,before_revision,after_secret_id,after_revision FROM main.work_effect_rotations;";
        using var rows = command.ExecuteReader(); var count = 0;
        while (rows.Read())
        {
            if (++count > 100000) throw WorkEffectRows.Invalid();
            var claim = WorkEffectRows.Require(connection, transaction, Guid.ParseExact(rows.GetString(0), "D"));
            var operation = Guid.ParseExact(rows.GetString(1), "D");
            var origin = WorkEffectInventory.Require(origins, Guid.ParseExact(rows.GetString(2), "D"),
                Guid.ParseExact(rows.GetString(3), "D"), Guid.ParseExact(rows.GetString(4), "D"), claim.Key);
            var reference = SecretDocumentRows.Find(connection, transaction, operation) ?? throw WorkEffectRows.Invalid();
            var source = new SecretDocumentFormat(instance, contracts).Read(reference);
            if (source.Document.ExtensionId != claim.Key.ExtensionId || source.Document.SubjectId != "extension:" + claim.Key.ExtensionId ||
                SecretDocumentRows.Status(connection, transaction, source) != "completed") throw WorkEffectRows.Invalid();
            var change = source.Document.Secrets.SingleOrDefault(value => value.SubjectId == origin.TaskId && value.Field == claim.Key.Field);
            if (change is null || change.Disposition != SecretDisposition.Set || change.Before.SecretId?.ToString("D") != rows.GetString(5) ||
                change.Before.Revision != rows.GetInt64(6) || change.After.SecretId?.ToString("D") != rows.GetString(7) ||
                change.After.Revision != rows.GetInt64(8)) throw WorkEffectRows.Invalid();
        }
    }
}
