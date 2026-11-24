using NexusPipeline.Core.Automation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Automation;

internal static class SpecializedScriptPolicy
{
    internal static IDisposable? HoldAndRequire(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        PayloadApplyRoots? roots, IEnumerable<AutomationScript> scripts)
    {
        Listings? listings = null;
        try
        {
            foreach (var script in scripts.Where(script => script.Kind == AutomationKind.Specialized))
            {
                listings ??= new(instance, database, contracts, roots ?? throw new IOException("automation_extension_roots_required"));
                Require(script, listings.ReadCurrent(script.ExtensionId!));
            }
            return listings;
        }
        catch { listings?.Dispose(); throw; }
    }

    private sealed class Listings(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        PayloadApplyRoots roots) : IDisposable
    {
        private readonly ExtensionInstalledEvidence _installed = new(instance, database, contracts, roots);
        private readonly Dictionary<ExtensionInstalledMetadata, (TreeReadLease Tree, ExtensionManifest Manifest)> _verified = [];
        private readonly List<TreeReadLease> _held = [];

        internal ExtensionManifest ReadCurrent(string extensionId)
        {
            var installed = _installed.ReadCurrent(extensionId);
            if (installed?.Metadata.IsInstalled != true) throw new IOException("automation_extension_missing");
            if (_verified.TryGetValue(installed.Metadata, out var listing))
            {
                listing.Tree.RequireNamespace();
                return listing.Manifest;
            }
            listing = ExtensionDetailsReader.HoldListing(installed, contracts);
            _held.Add(listing.Tree);
            _verified.Add(installed.Metadata, listing);
            return listing.Manifest;
        }

        public void Dispose()
        {
            for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        }
    }

    internal static void Require(AutomationScript script, ExtensionManifest manifest)
    {
        if (manifest.ExtensionId != script.ExtensionId) throw new IOException("automation_extension_unbound");
        var policy = manifest.Specialized?.InstancePolicy;
        if (policy is null) return;
        if (policy.RequiresConfigurationSync && !script.UpdateConfigurationAtFinish ||
            script.Target is { } target && (target.Kind == AutomationTargetKind.Android && !policy.SupportsAndroid ||
                target.Pc is { } pc && policy.SelfManagedPcLaunch &&
                    (target.LaunchByHost || target.WaitSeconds != 0 || !pc.Arguments.IsEmpty)))
            throw new AutomationDefinitionException("automation_specialized_policy_conflict");
    }
}
