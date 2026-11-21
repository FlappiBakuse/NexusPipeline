using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
internal sealed class DraftProjectSecrets
{
    internal const string PublicationKind = "project.draft-secret";
    private sealed record Field(SecretFieldReference State, ProtectedSecretPayload? Payload);
    private readonly ImmutableDictionary<string, Field> _fields;
    private readonly IReadOnlyDictionary<string, string> _proofs;
    private readonly string _provider;
    private readonly Guid _binding;

    private DraftProjectSecrets(string provider, Guid binding, ImmutableDictionary<string, Field> fields, IReadOnlyDictionary<string, string> proofs)
    { _provider = provider; _binding = binding; _fields = fields; _proofs = proofs; }

    internal ProjectInputState[] States => _fields.Select(pair => new ProjectInputState(pair.Key, pair.Value.State.Revision,
        pair.Value.Payload is not null, pair.Value.Payload is not null)).OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();

    internal static DraftProjectSecrets Prepare(RuntimeInstance instance, string provider, Guid binding, IReadOnlyCollection<string> keys,
        IReadOnlyList<ProjectProtectedEdit> edits, DraftProjectSecrets? previous, IReadOnlyDictionary<string, string> proofs)
    {
        if (edits.Any(item => !keys.Contains(item.Intent.Key))) throw new IOException("project_protected_input_unmapped");
        var fields = ImmutableDictionary.CreateBuilder<string, Field>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var before = previous?._fields.GetValueOrDefault(key) ?? new Field(new(0, null), null);
            var edit = edits.SingleOrDefault(item => item.Intent.Key == key);
            if (edit is null) { fields.Add(key, before); continue; }
            Guid? id = edit.Intent.Disposition == SecretDisposition.Set ? Guid.NewGuid() : null;
            var next = SecretMutationPolicy.Apply(before.State, edit.Intent.ExpectedRevision, edit.Intent.Disposition, id);
            var payload = id is { } selected ? new UserSecretProtector().Protect(instance.InstanceId, selected, edit.Value!) :
                edit.Intent.Disposition == SecretDisposition.Keep ? before.Payload : null;
            fields.Add(key, new(next, payload));
        }
        return new(provider, binding, fields.ToImmutable(), proofs);
    }

    internal static IReadOnlyDictionary<string, string> Proofs(RuntimeInstance instance, StateDatabase database,
        Guid operation, string provider, Guid binding, IReadOnlyList<ProjectProtectedEdit> edits)
    {
        var proofs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!edits.Any(edit => edit.Value is not null)) return proofs;
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        foreach (var edit in edits.Where(item => item.Value is not null))
        {
            var key = new SecretFieldKey(provider, binding, edit.Intent.Key);
            proofs.Add(edit.Intent.Key, SecretReplayProof.Compute(instance, connection,
                SecretFieldStore.Identity(operation, key, edit.Intent.ExpectedRevision, edit.Intent.Disposition), edit.Value!));
        }
        return proofs;
    }

    internal IReadOnlyDictionary<string, string> ReplayProofs => _proofs;
    internal bool HasChanges => _fields.Values.Any(item => item.State.Revision != 0);

    internal void RequireProofs(IReadOnlyDictionary<string, string>? proofs)
    {
        if ((proofs?.Count ?? 0) != _proofs.Count || proofs?.Any(item => !_proofs.TryGetValue(item.Key, out var prior) ||
            !SecretReplayProof.Matches(prior, item.Value)) == true)
            throw new OperationConflictException();
    }

    internal IEnumerable<(Guid Id, ProtectedSecretPayload Payload)> Payloads => _fields.Values.Where(entry => entry.Payload is not null)
        .Select(entry => (entry.State.SecretId!.Value, entry.Payload!));

    internal void Publish(SqliteConnection connection, SqliteTransaction transaction, Guid parent, DateTimeOffset acceptedAt)
    {
        foreach (var (name, entry) in _fields)
        {
            var key = new SecretFieldKey(_provider, _binding, name);
            SecretFieldRows.RequireReference(connection, transaction, key, new(0, null));
            if (entry.State.Revision == 0) continue;
            var id = SecretDocumentFormat.Child(parent, _binding, name);
            var disposition = entry.Payload is not null ? SecretDisposition.Set : entry.State.Revision == 0 ? SecretDisposition.Keep : SecretDisposition.Clear;
            var ordinary = SecretFieldStore.Identity(id, key, 0, disposition);
            var operation = new OperationIdentity(id, ordinary.SubjectId, ordinary.RequestDigest, PublicationKind);
            if (!OperationRows.Accept(connection, transaction, operation, acceptedAt).IsNew) throw new OperationConflictException();
            SecretPayloadIdentity? payload = null;
            if (entry.Payload is { } data && entry.State.SecretId is { } secret)
            {
                payload = new(secret, data.ProtectionScheme, data.OwnerSid, data.Sha256, data.Ciphertext.Length);
                using var query = connection.CreateCommand(); query.Transaction = transaction;
                query.CommandText = "INSERT INTO system.secret_payloads(secret_id,preparation_operation_id,protection_scheme,owner_sid,digest,size_bytes,state) VALUES($id,$operation,$scheme,$owner,$digest,$size,'prepared');";
                query.Parameters.AddWithValue("$id", secret.ToString("D")); query.Parameters.AddWithValue("$operation", id.ToString("D"));
                query.Parameters.AddWithValue("$scheme", data.ProtectionScheme); query.Parameters.AddWithValue("$owner", data.OwnerSid);
                query.Parameters.AddWithValue("$digest", data.Sha256); query.Parameters.AddWithValue("$size", data.Ciphertext.Length);
                query.ExecuteNonQuery();
            }
            SecretFieldStore.WriteResult(connection, transaction, id, key, SecretMutationPolicy.Apply(new(0, null), 0, disposition,
                entry.State.SecretId), payload, payload is not null, null);
        }
    }
}
