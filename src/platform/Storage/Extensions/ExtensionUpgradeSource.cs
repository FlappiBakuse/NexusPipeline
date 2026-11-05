using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class ExtensionUpgradeSource
{
    internal static bool AllowsSameVersion(ExtensionStoreChannel? candidate, ExtensionStoreChannel? original) =>
        candidate == ExtensionStoreChannel.Develop || original == ExtensionStoreChannel.Develop;

    internal static bool AllowsSameVersion(RuntimeInstance instance, ContractReader contracts, PayloadApplyRoots roots,
        ExtensionPayloadSource source)
    {
        if (source.Action != PayloadAction.Upgrade || source.Package is null) return false;
        var previous = new InstalledFootprintCodec(instance, roots, contracts)
            .ReadMetadata(source.OriginalFootprint ?? throw new IOException("extension_original_source_required"));
        if (previous.ExtensionId != source.ExtensionId || previous.Revision != source.ExpectedInstalledRevision || !previous.IsInstalled)
            throw new IOException("extension_original_source_unbound");
        var original = new PayloadSourceCodec(instance, contracts).ReadMetadata(previous.Source);
        if (original.JournalId != previous.JournalId || original.ExtensionId != previous.ExtensionId || original.Package is null)
            throw new IOException("extension_original_source_unbound");
        return AllowsSameVersion(source.Package.Catalog.Identity.Channel, original.Package.Catalog.Identity.Channel);
    }
}
