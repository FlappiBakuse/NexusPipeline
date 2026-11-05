using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class InstallationCommitCodec(RuntimeInstance instance, ContractReader contracts)
{
    internal const string Format = "nexus.extension.installation-commit";
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal ExtensionInstallationCommit ReadMetadata(BlobReference reference)
    {
        RequireReference(reference);
        using var input = _blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var strict = StrictJson.Parse(bytes);
        var value = JsonSerializer.Deserialize<ExtensionInstallationCommit>(bytes, Options) ?? throw Invalid();
        Validate(value);
        if (!JsonElement.DeepEquals(strict.RootElement, JsonSerializer.SerializeToElement(value, Options))) throw Invalid();
        return value;
    }

    internal byte[] EncodeMetadata(ExtensionInstallationCommit value)
    {
        Validate(value);
        return BoundedJson.Serialize(value, Options);
    }

    internal void RequireBinding(ExtensionInstallationCommit value, FrozenPayloadApply plan,
        ExtensionPayloadSource source, ExtensionPhysicalEntry physical,
        ExtensionInstalledFootprint footprint, BlobReference footprintReference,
        ExtensionActivationPreparation? preparation, BlobReference? preparationReference)
    {
        Validate(value);
        if (value.OperationId != plan.OperationId || value.JournalId != plan.JournalId ||
            value.Source != plan.ExtensionSource || value.Source != footprint.Source ||
            value.PhysicalResult != physical.Intent.Artifact || value.PhysicalResult != footprint.PhysicalResult ||
            value.Footprint != footprintReference || value.InstalledRevision != footprint.Revision ||
            value.InstalledRevision != checked(source.ExpectedInstalledRevision + 1) ||
            value.ActivationPreparation != preparationReference ||
            physical.Record.Stage != ExtensionPhysicalCodec.Sequence(plan)[^1] ||
            value.SettingsBeforeRevision != source.SettingsRevision || value.DesiredEnabled != source.DesiredEnabled ||
            value.InitializeEnabledPreference != source.InitializeEnabledPreference ||
            source.OperationId != plan.OperationId || source.JournalId != plan.JournalId ||
            footprint.OperationId != plan.OperationId || footprint.JournalId != plan.JournalId ||
            (plan.Action == PayloadAction.Remove || !source.DesiredEnabled
                ? value.ActivationState != "not-attempted" : value.ActivationState != "published")) throw Invalid();
        if (preparation is null)
        {
            if (preparationReference is not null || value.ActivationState != "not-attempted" ||
                value.BeforeRevision != physical.Record.BeforeRevision + 1) throw Invalid();
        }
        else
        {
            if (preparationReference is null || value.ActivationState != "published" ||
                preparation.InstanceId != instance.InstanceId || preparation.OperationId != plan.OperationId ||
                preparation.JournalId != plan.JournalId || preparation.Source != value.Source ||
                preparation.PhysicalResult != physical.Intent.Artifact || preparation.ActivationId != value.ActivationId ||
                preparation.BeforeRevision != physical.Record.BeforeRevision + 1 ||
                value.BeforeRevision != preparation.BeforeRevision + 1) throw Invalid();
        }
    }

    internal static bool IsParticipant(JsonElement participant) =>
        participant.TryGetProperty("identity", out var identity) && identity.TryGetProperty("format", out var format) &&
        format.ValueKind == JsonValueKind.String && format.GetString() == Format;

    internal static InstallationCommitIntent? ReadIntent(JsonElement root, string extensionId)
    {
        InstallationCommitIntent? found = null;
        foreach (var participant in root.GetProperty("participants").EnumerateArray())
        {
            if (!IsParticipant(participant)) continue;
            var identity = participant.GetProperty("identity");
            var value = identity.Deserialize<InstallationCommitIntent>(Options) ?? throw Invalid();
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

    internal static JsonObject Participant(InstallationCommitIntent intent, string extensionId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "directory", ["resourceKey"] = "extension:" + extensionId,
        ["identity"] = JsonSerializer.SerializeToNode(intent, Options)
    };

    internal static ImmutableArray<BlobReference> References(ExtensionInstallationCommit value) =>
        value.ActivationPreparation is { } preparation
            ? [value.Source, value.PhysicalResult, value.Footprint, preparation]
            : [value.Source, value.PhysicalResult, value.Footprint];

    private void Validate(ExtensionInstallationCommit value)
    {
        if (value is null || value.Format != Format || value.Version != 1 || value.InstanceId != instance.InstanceId ||
            value.OperationId == Guid.Empty || value.JournalId == Guid.Empty ||
            value.BeforeRevision is < 1 or > 9007199254740990 ||
            value.InstalledRevision is < 1 or > 9007199254740991 ||
            value.SettingsBeforeRevision is < 0 or > 9007199254740990 ||
            value.SettingsAfterRevision != value.SettingsBeforeRevision + (value.InitializeEnabledPreference ? 1 : 0) ||
            value.InitializeEnabledPreference && !value.DesiredEnabled ||
            value.ActivationState is not ("published" or "not-attempted") ||
            (value.ActivationState == "published" ? value.ActivationId is null || value.ActivationId == Guid.Empty :
                value.ActivationId is not null)) throw Invalid();
        RequireReference(value.Source); RequireReference(value.PhysicalResult); RequireReference(value.Footprint);
        if (value.ActivationPreparation is { } preparation) RequireReference(preparation);
        if ((value.ActivationState == "published") != (value.ActivationPreparation is not null)) throw Invalid();
    }

    private static void RequireReference(BlobReference reference)
    {
        if (reference is null || reference.SizeBytes is < 1 or > 2097152 ||
            reference.Sha256 is not { Length: 64 } || reference.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            reference.RelativePath != "blobs/plans/" + reference.Sha256 + ".json") throw Invalid();
    }
    private static IOException Invalid() => new("extension_installation_commit_unbound");
}
