using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

internal sealed record SecretDocumentMutation(Guid SubjectId, string Field, Guid OperationId, SecretDisposition Disposition,
    SecretFieldReference Before, SecretFieldReference After, bool ResultReadable, string? ReplayProof, Guid? JournalId, BlobReference? Preparation,
    SecretPreparationDefsRetirement? Retirement);
internal sealed record SecretDocumentPublication(string Format, int Version, Guid InstanceId, Guid OperationId, string SubjectId,
    string RequestDigest, string ExtensionId, string Name, long ExpectedRevision, BlobReference Content,
    ImmutableArray<SecretDocumentMutation> Secrets)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectChoicePublication? ProjectChoice { get; init; }
}
internal sealed record BoundSecretDocument(BlobReference Reference, SecretDocumentPublication Document);

[SupportedOSPlatform("windows")]
internal sealed class SecretDocumentFormat(RuntimeInstance instance, ContractReader contracts)
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false, MaxDepth = 32 };

    internal BoundSecretDocument Persist(SecretDocumentPublication publication)
    {
        Validate(publication);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(publication, Options);
        using var parsed = StrictJson.Parse(bytes);
        return new(new ImmutableBlobStore(instance, contracts).PersistPlan(bytes), publication);
    }

    internal BoundSecretDocument Read(BlobReference reference)
    {
        if (reference.RelativePath != $"blobs/plans/{reference.Sha256}.json" || reference.SizeBytes is < 1 or > 1048576)
            throw Invalid();
        using var stream = new ImmutableBlobStore(instance, contracts).OpenVerified(reference);
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        using var parsed = StrictJson.Parse(bytes);
        var publication = JsonSerializer.Deserialize<SecretDocumentPublication>(bytes, Options) ?? throw Invalid();
        Validate(publication);
        return new(reference, publication);
    }

    internal byte[] ReadContent(SecretDocumentPublication publication)
    {
        using var stream = new ImmutableBlobStore(instance, contracts).OpenVerified(publication.Content);
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        using var parsed = StrictJson.Parse(bytes);
        return bytes;
    }

    private void Validate(SecretDocumentPublication value)
    {
        if (value.Format != "nexus.secret.document-publication" || value.Version != 1 || value.InstanceId != instance.InstanceId ||
            value.OperationId == Guid.Empty || value.Content is null || value.Content.SizeBytes is < 1 or > 1048576 ||
            value.Content.RelativePath != $"blobs/content/{value.Content.Sha256}.bin" || !FileEffectRows.IsDigest(value.Content.Sha256) ||
            value.ExpectedRevision is < 0 or >= ExtensionDocumentRows.MaximumRevision || value.Secrets.IsDefault || value.Secrets.Length > 32)
            throw Invalid();
        _ = new OperationIdentity(value.OperationId, value.SubjectId, value.RequestDigest, "extension.document.write");
        Core.Extensions.ExtensionIdentifiers.Require(value.ExtensionId); ExtensionDataNames.Require(value.Name);
        if (value.ProjectChoice is { } project) ProjectChoicePublisher.Validate(project, value.OperationId, value.Name, value.Content);
        else if (value.Name.StartsWith("project-choice-", StringComparison.Ordinal)) throw Invalid();
        var ordered = value.Secrets.OrderBy(change => change.SubjectId).ThenBy(change => change.Field, StringComparer.Ordinal).ToArray();
        if (!ordered.SequenceEqual(value.Secrets) || value.Secrets.Select(change => (change.SubjectId, change.Field)).Distinct().Count() != value.Secrets.Length)
            throw Invalid();
        foreach (var change in value.Secrets)
        {
            _ = new SecretFieldKey(value.ExtensionId, change.SubjectId, change.Field);
            if (!Enum.IsDefined(change.Disposition) || change.OperationId != Child(value.OperationId, change.SubjectId, change.Field) ||
                change.OperationId == value.OperationId || change.Before.Revision is < 0 or >= SecretFieldReference.MaximumRevision ||
                change.After != SecretMutationPolicy.Apply(change.Before, change.Before.Revision, change.Disposition,
                    change.Disposition == SecretDisposition.Set ? change.After.SecretId : null) ||
                (change.Disposition == SecretDisposition.Set) != (change.JournalId is not null && change.Preparation is not null && FileEffectRows.IsDigest(change.ReplayProof ?? "")) ||
                change.Disposition != SecretDisposition.Set && (change.ReplayProof is not null || change.JournalId is not null || change.Preparation is not null) ||
                (change.Disposition != SecretDisposition.Keep && change.Before.SecretId is not null) != (change.Retirement is not null)) throw Invalid();
        }
        if (Digest(value) != value.RequestDigest) throw Invalid();
    }

    internal static string Digest(SecretDocumentPublication value)
    {
        var ordinary = new
        {
        value.InstanceId, value.SubjectId, value.ExtensionId, value.Name, value.ExpectedRevision, sha256 = value.Content.Sha256,
        secrets = value.Secrets.Select(change => new { change.SubjectId, change.Field, expectedRevision = change.Before.Revision,
            change.Disposition, change.ReplayProof })
        };
        return ExtensionDocumentRows.Digest(value.ProjectChoice is null ? JsonSerializer.SerializeToUtf8Bytes(ordinary, Options) :
            JsonSerializer.SerializeToUtf8Bytes(new { ordinary, value.ProjectChoice }, Options));
    }

    internal static OperationIdentity Identity(SecretDocumentPublication value) =>
        new(value.OperationId, value.SubjectId, value.RequestDigest, "extension.document.write");
    internal static Guid Child(Guid parent, Guid subject, string field)
    {
        var bytes = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { kind = "secret.document.child", parent, subject, field }, Options));
        return new Guid(bytes.AsSpan(0, 16));
    }
    internal static IOException Invalid() => new("secret_document_publication_unrecognized");
}
