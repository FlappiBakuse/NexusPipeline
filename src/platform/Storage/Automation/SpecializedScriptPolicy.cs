using NexusPipeline.Core.Automation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Automation;

internal static class SpecializedScriptPolicy
{
    internal static void Require(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        PayloadApplyRoots? roots, IEnumerable<AutomationScript> scripts)
    {
        foreach (var script in scripts.Where(script => script.Kind == AutomationKind.Specialized))
        {
            var installed = new ExtensionInstalledEvidence(instance, database, contracts,
                roots ?? throw new IOException("automation_extension_roots_required")).ReadCurrent(script.ExtensionId!)
                ?? throw new IOException("automation_extension_missing");
            if (!installed.Metadata.IsInstalled) throw new IOException("automation_extension_missing");
            Require(script, ExtensionDetailsReader.ReadListing(installed, contracts));
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
