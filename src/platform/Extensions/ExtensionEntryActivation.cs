using System.Text.Json;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Extensions;

internal sealed record ExtensionEntryActivation(ExtensionManifest Manifest, Func<ExtensionScope, CancellationToken, ValueTask> Callback)
{
    internal static ExtensionEntryActivation Managed(ContractReader reader, ReadOnlyMemory<byte> bytes, string extensionId,
        Func<ExtensionManifest, ExtensionScope, IExtension> factory)
    {
        var manifest = new ExtensionManifestAdmission(reader).ReadCurrent(bytes, extensionId);
        if (manifest.Kind != "managed-code") throw new JsonException("extension_identity_mismatch");
        return new(manifest, async (scope, cancellation) =>
        {
            var extension = factory(manifest, scope) ?? throw new InvalidOperationException("extension_factory_returned_null");
            await extension.ActivateAsync(scope, cancellation).ConfigureAwait(false);
        });
    }

    internal static ExtensionEntryActivation Specialized(ContractReader reader, ReadOnlyMemory<byte> bytes, string extensionId,
        Func<ExtensionManifest, ExtensionScope, CancellationToken, ValueTask> activate)
    {
        var manifest = new ExtensionManifestAdmission(reader).ReadCurrent(bytes, extensionId);
        if (manifest.Kind != "data-specialized") throw new JsonException("extension_identity_mismatch");
        return new(manifest, (scope, cancellation) => activate(manifest, scope, cancellation));
    }
}
