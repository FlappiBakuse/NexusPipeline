using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class ExtensionWorkCodec(RuntimeInstance instance, StateDatabase database, ContractReader contracts, PayloadApplyRoots roots)
{
    internal const string ProofFormat = "nexus.extension.work-proof";
    internal const string PageFormat = "nexus.extension.work-page";
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal BlobReference PersistProof(ExtensionWorkProof value)
    {
        _ = BindProof(value);
        return _blobs.PersistPlan(BoundedJson.Serialize(value, Options));
    }

    internal BlobReference PersistPage(ExtensionWorkPage value)
    {
        RequirePage(value); _ = ReadProof(value.Proof);
        return _blobs.PersistPlan(BoundedJson.Serialize(value, Options));
    }

    internal BoundExtensionWork ReadProof(BlobReference reference)
    {
        var value = Read<ExtensionWorkProof>(reference);
        return new(reference, value, BindProof(value));
    }

    internal BoundWorkPage ReadPage(BlobReference reference)
    {
        var value = Read<ExtensionWorkPage>(reference); RequirePage(value);
        var origin = ReadProof(value.Proof);
        if (value.InstanceId != instance.InstanceId || value.Indices.Any(index => index >= origin.Proof.Nodes.Length)) throw Invalid();
        return new(reference, value, origin);
    }

    private BoundExtensionPayload BindProof(ExtensionWorkProof value)
    {
        if (value is null || value.Format != ProofFormat || value.Version != 1 ||
            value.InstanceId != instance.InstanceId || database.InstanceId != instance.InstanceId ||
            database.RuntimeRoot != instance.RuntimeRoot || value.ProducerOperationId == Guid.Empty ||
            value.ProducerJournalId == Guid.Empty || value.ProducerRevision < 1 || value.Nodes.IsDefaultOrEmpty ||
            value.Nodes.Length > WorkNodeDerivation.MaximumNodes) throw Invalid();
        Reference(value.ProducerPlan); Reference(value.TerminalArtifact);
        var bound = new ExtensionPayloadBinding(instance, database, contracts, roots).Read(value.ProducerJournalId, value.ProducerPlan);
        var artifact = bound.CommitIntent?.Artifact ?? (!bound.Restoration.IsEmpty ? bound.Restoration[^1].Intent.Artifact : null);
        if (bound.Plan.OperationId != value.ProducerOperationId || bound.Registered.Revision != value.ProducerRevision ||
            value.TerminalArtifact != artifact || value.ProducerSha256 != Convert.ToHexStringLower(SHA256.HashData(bound.Latest.Export())) ||
            value.WorkspacePath != ExtensionWorkspacePath.For(bound.Plan) || value.ParentIdentity != bound.Plan.ParentIdentity ||
            bound.Physical.IsEmpty || value.WorkspaceIdentity != bound.Physical[^1].Record.WorkspaceIdentity ||
            !value.Nodes.SequenceEqual(WorkNodeDerivation.Derive(bound, value.Layout))) throw Invalid();
        return bound;
    }

    private static void RequirePage(ExtensionWorkPage value)
    {
        if (value is null || value.Format != PageFormat || value.Version != 1 || value.InstanceId == Guid.Empty ||
            value.OperationId == Guid.Empty || value.JournalId == Guid.Empty || value.Indices.IsDefaultOrEmpty ||
            value.Indices.Length > 256 || value.Indices.Any(item => item is < 0 or >= WorkNodeDerivation.MaximumNodes) ||
            !value.Indices.SequenceEqual(value.Indices.Order()) || value.Indices.Distinct().Count() != value.Indices.Length ||
            (value.PreviousJournalId is null) != (value.PreviousPlan is null) ||
            value.PreviousJournalId is { } previous && (previous == Guid.Empty || previous == value.JournalId)) throw Invalid();
        Reference(value.Proof); if (value.PreviousPlan is { } reference) Reference(reference);
    }

    private T Read<T>(BlobReference reference)
    {
        Reference(reference); using var input = _blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var strict = StrictJson.Parse(bytes);
        var value = JsonSerializer.Deserialize<T>(bytes, Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(strict.RootElement, JsonSerializer.SerializeToElement(value, Options))) throw Invalid();
        return value;
    }

    internal static void Reference(BlobReference value)
    {
        if (value is null || value.SizeBytes is < 1 or > 2097152 || value.Sha256 is not { Length: 64 } ||
            value.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            value.RelativePath != "blobs/plans/" + value.Sha256 + ".json") throw Invalid();
    }
    private static IOException Invalid() => new("extension_work_document_unbound");
}
