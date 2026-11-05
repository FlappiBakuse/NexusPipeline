using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionProviderRelationship
{
    internal static void Require(ExtensionManifest consumer, ExecutionPlanStep step)
    {
        if (step.Adapter is null || step.ExtensionId != consumer.ExtensionId || consumer.Kind is not ("data-specialized" or "managed-code")) throw Invalid();
        var use = consumer.Specialized?.ExecutionProvider;
        if (use is null)
        {
            if (step.Provider is not null || step.EngineKind != "process") throw Invalid();
            return;
        }
        if (step.EngineKind != "extensionWorker" || step.Provider is not { } provider ||
            provider.ExtensionId != use.ExtensionId || provider.EntryId != use.EntryId ||
            step.EntryId != use.EntryId || consumer.Kind == "data-specialized" && provider.ExtensionId == consumer.ExtensionId) throw Invalid();
    }

    private static IOException Invalid() => new("execution_provider_relationship_unbound");
}
