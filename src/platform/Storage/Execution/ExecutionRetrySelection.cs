using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Settings;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionRetrySelection
{
    internal static ExecutionPlanExplanation Read(ContractReader contracts, ExecutionPlan total, ReadOnlyMemory<byte> definitions,
        ExecutionSelectionRequest request, HostSettingsSnapshot settings)
    {
        var roots = total.RootBindings.ToDictionary(root => Guid.Parse(root.RootId), root => root.Path);
        var imported = new AutomationBundleCodec(contracts).Decode(definitions, roots, request.ExpectedCatalogRevision).Definitions;
        var revisions = total.SourceRevisions.ToDictionary(item => (item.EntityKind, Guid.Parse(item.EntityId)), item => item.Revision);
        long Rev(string kind, Guid id) => revisions.TryGetValue((kind, id), out var value) ? value : throw Invalid();
        var catalog = new AutomationCatalog(request.ExpectedCatalogRevision,
            imported.Roots.Select(item => item with { Revision = Rev("root", item.Id) }).ToImmutableArray(),
            imported.Scripts.Select(item => item with { Revision = Rev("script", item.Id) }).ToImmutableArray(),
            imported.Users.Select(item => item with { Revision = Rev("user", item.Id) }).ToImmutableArray(),
            imported.Bindings.Select(item => item with { Revision = Rev("binding", item.Id) }).ToImmutableArray(),
            imported.Queues.Select(item => item with { Revision = Rev("queue", item.Id) }).ToImmutableArray(), []);
        var selection = ExecutionSelectionPolicy.Select(catalog, settings, request);
        if (!selection.Items.Select(item => item.Binding.Id.ToString("D")).SequenceEqual(total.Selection.Select(item => item.BindingId)))
            throw Invalid();
        var saved = total.Selection.ToDictionary(item => item.StepId, StringComparer.Ordinal);
        var readiness = selection.Items.Where(item => item.Disposition == ExecutionSelectionDisposition.Candidate).Select(item =>
        {
            var prior = saved[item.StepId];
            if (prior.Disposition != "runnable" || prior.ReasonCode != ExecutionSelectionPolicy.InputsVerified) throw Invalid();
            return new ExecutionItemReadiness(item, true, ExecutionSelectionPolicy.InputsVerified);
        }).ToImmutableArray();
        var explanation = ExecutionSelectionPolicy.Explain(selection, readiness);
        if (explanation.HasBlockedItems || !explanation.HasRunnable ||
            !explanation.Items.Select(item => item.Disposition.ToString().ToLowerInvariant()).SequenceEqual(total.Selection.Select(item => item.Disposition)))
            throw Invalid();
        return explanation;
    }

    private static IOException Invalid() => new("execution_retry_selection_unbound");
}
