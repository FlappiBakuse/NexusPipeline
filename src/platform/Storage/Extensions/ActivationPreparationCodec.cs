using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class ActivationPreparationCodec(RuntimeInstance instance, ContractReader contracts)
{
    internal const string Format = "nexus.extension.activation-preparation";
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal ExtensionActivationPreparation ReadMetadata(BlobReference reference)
    {
        RequireReference(reference);
        using var input = _blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var strict = StrictJson.Parse(bytes);
        var value = JsonSerializer.Deserialize<ExtensionActivationPreparation>(bytes, Options) ?? throw Invalid();
        Validate(value);
        if (!JsonElement.DeepEquals(strict.RootElement, JsonSerializer.SerializeToElement(value, Options))) throw Invalid();
        return value;
    }

    internal byte[] EncodeMetadata(ExtensionActivationPreparation value)
    {
        Validate(value);
        return BoundedJson.Serialize(value, Options);
    }

    internal void RequireBinding(ExtensionActivationPreparation value, FrozenPayloadApply plan,
        ExtensionPayloadSource source, ExtensionPhysicalEntry physical)
    {
        Validate(value);
        if (source.Action == PayloadAction.Remove || !source.DesiredEnabled ||
            value.OperationId != plan.OperationId || value.JournalId != plan.JournalId ||
            value.Source != plan.ExtensionSource || value.PhysicalResult != physical.Intent.Artifact ||
            value.BeforeRevision != physical.Record.BeforeRevision + 1 ||
            physical.Record.Stage != ExtensionPhysicalCodec.Sequence(plan)[^1]) throw Invalid();
    }

    internal static bool IsParticipant(JsonElement participant) =>
        participant.TryGetProperty("identity", out var identity) && identity.TryGetProperty("format", out var format) &&
        format.ValueKind == JsonValueKind.String && format.GetString() == Format;

    internal static ActivationPreparationIntent? ReadIntent(JsonElement root, string extensionId)
    {
        ActivationPreparationIntent? found = null;
        foreach (var participant in root.GetProperty("participants").EnumerateArray())
        {
            if (!IsParticipant(participant)) continue;
            var identity = participant.GetProperty("identity");
            var value = identity.Deserialize<ActivationPreparationIntent>(Options) ?? throw Invalid();
            if (found is not null || !JsonElement.DeepEquals(identity, JsonSerializer.SerializeToElement(value, Options)) ||
                value.Format != Format || value.Version != 1 || value.BeforeRevision < 1 ||
                participant.GetProperty("kind").GetString() != "directory" ||
                participant.GetProperty("resourceKey").GetString() != "extension:" + extensionId ||
                !Guid.TryParseExact(participant.GetProperty("participantId").GetString(), "D", out var participantId) ||
                participantId == Guid.Empty) throw Invalid();
            RequireReference(value.Artifact); found = value;
        }
        return found;
    }

    internal static JsonObject Participant(ActivationPreparationIntent intent, string extensionId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "directory", ["resourceKey"] = "extension:" + extensionId,
        ["identity"] = JsonSerializer.SerializeToNode(intent, Options)
    };

    private void Validate(ExtensionActivationPreparation value)
    {
        if (value is null || value.Format != Format || value.Version != 1 || value.InstanceId != instance.InstanceId ||
            value.OperationId == Guid.Empty || value.JournalId == Guid.Empty || value.ActivationId == Guid.Empty ||
            value.BeforeRevision is < 1 or > 9007199254740990) throw Invalid();
        RequireReference(value.Source); RequireReference(value.PhysicalResult);
    }
    private static void RequireReference(BlobReference reference)
    {
        if (reference is null || reference.SizeBytes is < 1 or > 2097152 ||
            reference.Sha256 is not { Length: 64 } || reference.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            reference.RelativePath != "blobs/plans/" + reference.Sha256 + ".json") throw Invalid();
    }
    private static IOException Invalid() => new("extension_activation_preparation_unbound");
}
