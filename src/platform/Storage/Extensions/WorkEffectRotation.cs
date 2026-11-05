using System.Runtime.Versioning;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record WorkEffectBinding(WorkEffectClaim Claim, IInstalledWork Work, SecretFieldKey Key, Guid SecretId, long SecretRevision,
    Extensibility.Scheduling.WorkEffectState State, long Epoch, bool IsPersisted);
internal sealed record PreparedEffectRotation(SecretFieldKey Key, Guid BeforeId, long BeforeRevision, string Family, IReadOnlyList<Guid> Claims);
internal sealed record CompletedEffectRotation(PreparedEffectRotation Before, Guid AfterId, long AfterRevision);

[SupportedOSPlatform("windows")]
internal sealed class WorkEffectRotation(RuntimeInstance instance, StateDatabase database, SecretFieldService secrets, ContractReader contracts)
{
    internal async ValueTask<IReadOnlyList<PreparedEffectRotation>> PrepareAsync(IInstalledWork work, IReadOnlyList<WorkEffectBinding> bindings,
        IReadOnlyList<SecretDocumentEdit> edits, CancellationToken cancellation)
    {
        var result = new List<PreparedEffectRotation>();
        foreach (var group in bindings.GroupBy(binding => binding.Key))
        {
            var edit = edits.SingleOrDefault(item => item.SubjectId == group.Key.SubjectId && item.Field == group.Key.Field);
            if (edit is null || edit.Disposition == SecretDisposition.Keep) continue;
            if (edit.Disposition != SecretDisposition.Set || edit.Value is null) throw new UnauthorizedAccessException("work_effect_rotation_forbidden");
            var current = await secrets.GetStateAsync(group.Key, cancellation).ConfigureAwait(false);
            if (!current.IsReadable || current.SecretId is not { } id || current.Revision != edit.ExpectedRevision ||
                group.Any(binding => binding.SecretId != id || binding.SecretRevision != current.Revision))
                throw new IOException("work_effect_secret_changed");
            using var before = await secrets.ReadAsync(group.Key, cancellation).ConfigureAwait(false);
            work.RequireCurrent();
            if (!work.CanRotateIdentity(group.Key.Field, before, edit.Value)) throw new UnauthorizedAccessException("work_effect_rotation_forbidden");
            var family = Family(work, group.Key, before);
            if (!SecretReplayProof.Matches(family, Family(work, group.Key, edit.Value)) || group.Any(binding => binding.Claim.Key.Family != family))
                throw new UnauthorizedAccessException("work_effect_rotation_identity_changed");
            cancellation.ThrowIfCancellationRequested(); work.RequireCurrent();
            result.Add(new(group.Key, id, current.Revision, family, group.Select(binding => binding.Claim.Id).Distinct().ToArray()));
        }
        return result;
    }

    internal async ValueTask<IReadOnlyList<CompletedEffectRotation>> FinishAsync(Guid operationId, string subject, IInstalledWork work,
        IReadOnlyList<PreparedEffectRotation> prepared, CancellationToken cancellation)
    {
        if (prepared.Count == 0) return [];
        BoundSecretDocument source;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        {
            var reference = SecretDocumentRows.Find(connection, null, operationId) ?? throw WorkEffectRows.Invalid();
            source = new SecretDocumentFormat(instance, contracts).Read(reference);
            using var transaction = connection.BeginTransaction(deferred: true);
            if (SecretDocumentRows.Status(connection, transaction, source) != "completed" || source.Document.SubjectId != subject ||
                source.Document.ExtensionId != prepared[0].Key.ExtensionId) throw WorkEffectRows.Invalid();
        }
        var results = new List<CompletedEffectRotation>();
        foreach (var item in prepared)
        {
            var change = source.Document.Secrets.SingleOrDefault(value => value.SubjectId == item.Key.SubjectId && value.Field == item.Key.Field);
            if (change is null || change.Disposition != SecretDisposition.Set || change.Before.SecretId != item.BeforeId ||
                change.Before.Revision != item.BeforeRevision || change.After.SecretId is not { } afterId ||
                change.After.Revision != item.BeforeRevision + 1) throw WorkEffectRows.Invalid();
            var current = await secrets.GetStateAsync(item.Key, cancellation).ConfigureAwait(false);
            if (current.SecretId != afterId || current.Revision != change.After.Revision || !current.IsReadable)
                throw new IOException("work_effect_secret_changed");
            using var material = await secrets.ReadAsync(item.Key, cancellation).ConfigureAwait(false);
            if (!SecretReplayProof.Matches(item.Family, Family(work, item.Key, material))) throw WorkEffectRows.Invalid();
            results.Add(new(item, afterId, current.Revision));
        }
        database.Write((connection, transaction) =>
        {
            work.RequireCurrent(); cancellation.ThrowIfCancellationRequested();
            foreach (var result in results)
                foreach (var claim in result.Before.Claims)
                {
                    using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "INSERT INTO work_effect_rotations VALUES($claim,$op,$source,$run,$attempt,$before,$revision,$after,$next);";
                    command.Parameters.AddWithValue("$claim", claim.ToString("D")); command.Parameters.AddWithValue("$op", operationId.ToString("D"));
                    command.Parameters.AddWithValue("$source", work.OperationId.ToString("D"));
                    command.Parameters.AddWithValue("$run", work.FrozenInvocation.RunId.ToString("D"));
                    command.Parameters.AddWithValue("$attempt", work.FrozenInvocation.AttemptId.ToString("D"));
                    command.Parameters.AddWithValue("$before", result.Before.BeforeId.ToString("D")); command.Parameters.AddWithValue("$revision", result.Before.BeforeRevision);
                    command.Parameters.AddWithValue("$after", result.AfterId.ToString("D")); command.Parameters.AddWithValue("$next", result.AfterRevision);
                    command.ExecuteNonQuery();
                }
            return true;
        });
        return results;
    }

    private string Family(IInstalledWork work, SecretFieldKey key, SecretValue material)
    {
        using var projected = work.ProjectIdentity(key.Field, material);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        return SecretReplayProof.Identity(instance, connection, key, work.RequireIdentity(), projected);
    }
}
