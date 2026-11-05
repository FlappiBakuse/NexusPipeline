using System.Text.Json;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Extensions;

public sealed class ExtensionManifestAdmission(ContractReader reader)
{
    public ExtensionManifest ReadCurrent(ReadOnlyMemory<byte> bytes, string expectedExtensionId)
    {
        var manifest = reader.Read<ExtensionManifest>(bytes, "extension-manifest.schema.json");
        if (manifest.ExtensionId != expectedExtensionId || manifest.SdkVersion != SdkIdentity.Version ||
            manifest.ContractFingerprint != SdkIdentity.ContractFingerprint)
            throw new JsonException("extension_contract_identity_mismatch");
        if (!Version.TryParse(manifest.MinHostVersion.Split('-')[0], out var minimumHost) || minimumHost > new Version(0, 18, 0))
            throw new JsonException("extension_host_version_unsupported");
        if (manifest.Kind == "data-specialized" && manifest.Specialized?.Compiler?.ExtensionId == manifest.ExtensionId)
            throw new JsonException("extension_compiler_dependency_invalid");
        if (manifest.Kind == "data-specialized" && manifest.Specialized?.ExecutionProvider?.ExtensionId == manifest.ExtensionId)
            throw new JsonException("extension_execution_dependency_invalid");
        if (manifest.Kind == "managed-code" && manifest.Specialized is { } driver &&
            (driver.Compiler?.ExtensionId != manifest.ExtensionId || driver.ExecutionProvider?.ExtensionId != manifest.ExtensionId ||
                manifest.Managed?.Compilers?.Contains(driver.Compiler.CompilerId, StringComparer.Ordinal) != true ||
                driver.ExecutionProvider.EntryId != manifest.Execution?.EntryId))
            throw new JsonException("extension_driver_declaration_unbound");
        if (ExtensionMetadataReader.Read(manifest.Metadata) is { } metadata &&
            (metadata.DisplayName != manifest.DisplayName || metadata.GameName != manifest.GameName || metadata.Summary != manifest.Summary ||
             metadata.DefaultLocale != manifest.Localization?.DefaultLocale ||
             !metadata.Locales.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(manifest.Localization.Locales.Keys)))
            throw new JsonException("extension_manifest_metadata_unbound");
        return manifest;
    }
}
