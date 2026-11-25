using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class ExtensionUpgradeSource
{
    // Versions admitted by the release schema have canonical numeric components.
    internal static bool IsNewer(string candidate, string installed)
    {
        var left = candidate.Split(['.', '-']);
        var right = installed.Split(['.', '-']);
        for (var index = 0; index < 3; index++)
        {
            var order = NumberOrder(left[index], right[index]);
            if (order != 0) return order > 0;
        }
        if (left.Length == 3 || right.Length == 3) return left.Length < right.Length;
        var stage = StringComparer.Ordinal.Compare(left[3], right[3]);
        return stage != 0 ? stage > 0 : NumberOrder(left[4], right[4]) > 0;

        static int NumberOrder(string first, string second) => first.Length != second.Length
            ? first.Length.CompareTo(second.Length) : StringComparer.Ordinal.Compare(first, second);
    }

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
        return AllowsSameVersion(source.Package.Catalog?.Identity.Channel, original.Package.Catalog?.Identity.Channel);
    }
}
