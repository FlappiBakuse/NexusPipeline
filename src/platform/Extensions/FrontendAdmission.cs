using System.Text.Json;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Extensions;

public sealed class FrontendAdmission(ContractReader reader)
{
    public FrontendHandshake ReadHandshake(ReadOnlyMemory<byte> manifestBytes, ReadOnlyMemory<byte> handshakeBytes, string extensionId)
    {
        var manifest = new ExtensionManifestAdmission(reader).ReadCurrent(manifestBytes, extensionId);
        var handshake = reader.Read<FrontendHandshake>(handshakeBytes, "frontend-handshake.schema.json");
        if (manifest.ExtensionId != extensionId || handshake.ExtensionId != extensionId ||
            manifest.Kind != "managed-code" || manifest.Frontend is null || !manifest.Capabilities.Contains("ui.contribute") ||
            handshake.SdkVersion != SdkIdentity.Version || handshake.ContractFingerprint != SdkIdentity.ContractFingerprint)
            throw new JsonException("frontend_contract_identity_mismatch");
        return handshake;
    }
}
