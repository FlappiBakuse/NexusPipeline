using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

internal sealed record SecretDocumentEdit(Guid SubjectId, string Field, long ExpectedRevision, SecretDisposition Disposition, SecretValue? Value);
internal sealed class PreparedSecretDocument(BoundSecretDocument source, ImmutableArray<PreparedSecretPayload> payloads,
    List<IDisposable> held) : IDisposable
{
    internal BoundSecretDocument Source { get; } = source;
    internal ImmutableArray<PreparedSecretPayload> Payloads { get; } = payloads;
    public void Dispose()
    {
        foreach (var payload in Payloads) payload.Dispose();
        for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
    }
}

[SupportedOSPlatform("windows")]
internal sealed class SecretDocumentPreparation(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal SecretDocumentPublication Inputs(Guid operationId, string subject, string extensionId, string name,
        long expectedRevision, ReadOnlyMemory<byte> json, IReadOnlyList<SecretDocumentEdit> edits, BoundSecretDocument? replay = null,
        Extensions.ProjectChoicePublication? projectChoice = null)
    {
        if (operationId == Guid.Empty || expectedRevision is < 0 or >= ExtensionDocumentRows.MaximumRevision ||
            json.Length is < 1 or > 1048576 || edits.Count > 32 || edits.Select(edit => (edit.SubjectId, edit.Field)).Distinct().Count() != edits.Count)
            throw new ArgumentException("secret_document_request_invalid");
        Core.Extensions.ExtensionIdentifiers.Require(extensionId); Extensions.ExtensionDataNames.Require(name);
        using var parsed = StrictJson.Parse(json);
        var content = new ImmutableBlobStore(instance, contracts).PersistContent(json, default);
        var changes = ImmutableArray.CreateBuilder<SecretDocumentMutation>();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        foreach (var edit in edits.OrderBy(value => value.SubjectId).ThenBy(value => value.Field, StringComparer.Ordinal))
        {
            var key = new SecretFieldKey(extensionId, edit.SubjectId, edit.Field);
            if (!Enum.IsDefined(edit.Disposition) || (edit.Disposition == SecretDisposition.Set) != (edit.Value is not null))
                throw new ArgumentException("secret_document_edit_invalid");
            var old = replay?.Document.Secrets.SingleOrDefault(change => change.SubjectId == edit.SubjectId && change.Field == edit.Field);
            var state = replay is null ? new SecretFieldStore(instance, database).GetStateAsync(key, default).GetAwaiter().GetResult() : null;
            if (replay is not null && old is null) throw new Core.Operations.OperationConflictException();
            var before = old?.Before ?? new SecretFieldReference(state!.Revision, state.SecretId);
            if (edit.ExpectedRevision != before.Revision) throw new InvalidOperationException("secret_revision_conflict");
            var child = SecretDocumentFormat.Child(operationId, edit.SubjectId, edit.Field);
            var proof = edit.Disposition == SecretDisposition.Set ? SecretReplayProof.Compute(instance, connection,
                SecretFieldStore.Identity(child, key, before.Revision, edit.Disposition), edit.Value!) : null;
            changes.Add(new(edit.SubjectId, edit.Field, child, edit.Disposition, before, before,
                edit.Disposition == SecretDisposition.Set || edit.Disposition == SecretDisposition.Keep && (old?.ResultReadable ?? state!.IsReadable),
                proof, null, null, null));
        }
        var initial = new SecretDocumentPublication("nexus.secret.document-publication", 1, instance.InstanceId, operationId, subject,
            new string('0', 64), extensionId, name, expectedRevision, content, changes.ToImmutable()) { ProjectChoice = projectChoice };
        return initial with { RequestDigest = SecretDocumentFormat.Digest(initial) };
    }

    internal PreparedSecretDocument Freeze(SecretDocumentPublication inputs, IReadOnlyList<SecretDocumentEdit> edits,
        ResourceOperationLease root, ResourceLeaseManager resources, CancellationToken cancellation)
    {
        var held = new List<IDisposable>(); var payloads = ImmutableArray.CreateBuilder<PreparedSecretPayload>();
        try
        {
            var changes = ImmutableArray.CreateBuilder<SecretDocumentMutation>();
            using var batch = new PreparedDocumentSecrets(instance, inputs.ExtensionId, SecretDocumentFormat.Identity(inputs), inputs.Secrets, root, resources);
            foreach (var change in inputs.Secrets)
            {
                cancellation.ThrowIfCancellationRequested();
                PreparedSecretPayload? payload = null; SecretFieldRetirement? retirement = null;
                if (change.Disposition == SecretDisposition.Set)
                {
                    var edit = edits.Single(value => value.SubjectId == change.SubjectId && value.Field == change.Field);
                    payload = new PreparationPlanCodec(instance, contracts, database).FreezeDocument(batch, change.SubjectId, change.Field, edit.Value!, cancellation);
                    payloads.Add(payload); retirement = payload.Retirement;
                }
                else if (change.Disposition == SecretDisposition.Clear && change.Before.SecretId is { } previous)
                { retirement = SecretFieldRetirement.Prepare(instance, database, change.OperationId, previous); held.Add(retirement); }
                var plan = payload?.Plan.ReadWire();
                changes.Add(change with { After = SecretMutationPolicy.Apply(change.Before, change.Before.Revision, change.Disposition,
                    plan is null ? null : Guid.Parse(plan.Candidate.SecretId)), JournalId = plan is null ? null : Guid.Parse(plan.JournalId),
                    Preparation = payload?.Reference, Retirement = retirement?.Describe() });
            }
            var source = new SecretDocumentFormat(instance, contracts).Persist(inputs with { Secrets = changes.ToImmutable() });
            var blobs = new ImmutableBlobStore(instance, contracts);
            held.Add(blobs.OpenVerified(source.Reference)); held.Add(blobs.OpenVerified(source.Document.Content));
            return new(source, payloads.ToImmutable(), held);
        }
        catch
        {
            foreach (var payload in payloads) payload.Dispose();
            for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
            throw;
        }
    }
}
