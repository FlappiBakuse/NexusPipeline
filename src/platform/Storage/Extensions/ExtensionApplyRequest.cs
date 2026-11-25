using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ExtensionApplyRequest(PayloadAction Action, string ExtensionId,
    long ExpectedInstalledRevision, long ExpectedSettingsRevision,
    ExtensionCatalogIdentity? Catalog, ExtensionCatalogEntry? Candidate)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? LocalApproval { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? LocalUpdateConfirmation { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ImportTarget { get; init; }

    internal static ExtensionApplyRequest FromSource(ExtensionPayloadSource source) => new(
        source.Action, source.ExtensionId, source.ExpectedInstalledRevision, source.SettingsRevision,
        source.Package?.Catalog?.Identity, source.Package?.Entry)
        { LocalApproval = source.Package?.LocalApproval, LocalUpdateConfirmation = source.LocalUpdateConfirmation, ImportTarget = source.ImportTarget };

    internal OperationIdentity Identity(Guid operationId, string subject)
    {
        if (!Enum.IsDefined(Action) || ExtensionId is not { Length: >= 1 and <= 80 } ||
            ExtensionId[0] is not (>= 'a' and <= 'z') || ExtensionId[^1] == '-' ||
            ExtensionId.Contains("--", StringComparison.Ordinal) ||
            ExtensionId.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) ||
            ExpectedInstalledRevision is < 0 or > 9007199254740991 ||
            ExpectedSettingsRevision is < 0 or > 9007199254740991 ||
            (Action == PayloadAction.Remove) != (Candidate is null) ||
            (Catalog is not null && (Candidate is null || LocalApproval is not null)) ||
            LocalApproval is not null && (Candidate is null || !LocalExtensionEvidence.ValidId(ExtensionId) ||
                !LocalExtensionEvidence.IsDigest(LocalApproval)) ||
            LocalUpdateConfirmation is not null && (Action != PayloadAction.Upgrade || Catalog is null ||
                !LocalExtensionEvidence.IsDigest(LocalUpdateConfirmation)) ||
            Action != PayloadAction.Remove && (Candidate!.ExtensionId != ExtensionId || (Catalog is null && LocalApproval is null)) ||
            ImportTarget && Action != PayloadAction.Install ||
            Action is PayloadAction.Upgrade or PayloadAction.Remove && ExpectedInstalledRevision == 0)
            throw new IOException("extension_apply_request_invalid");
        if (Catalog is not null) CatalogSequencePolicy.Require(Catalog, 1, null);
        return new(operationId, subject, Convert.ToHexStringLower(SHA256.HashData(CanonicalBytes())), "extension.apply");
    }
    private byte[] CanonicalBytes()
    {
        using var document = StrictJson.Parse(BoundedJson.Serialize(this));
        using var output = new MemoryStream();
        using var writer = new Utf8JsonWriter(output);
        Write(document.RootElement, writer);
        writer.Flush();
        return output.ToArray();
    }

    private static void Write(JsonElement value, Utf8JsonWriter writer)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                Write(property.Value, writer);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) Write(item, writer);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

}
