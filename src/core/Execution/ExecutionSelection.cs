using System.Collections.Immutable;
using NexusPipeline.Core.Automation;

namespace NexusPipeline.Core.Execution;

public enum ExecutionSelectionDisposition { Candidate, Skipped, Blocked }
public enum ExecutionExplanationDisposition { Runnable, Skipped, Blocked }
public sealed record ExecutionSelectionRevision(AutomationDefinitionKind Kind, Guid EntityId, long Revision);

public sealed class ExecutionSelectionItem
{
    public int Ordinal { get; }
    public string StepId => "binding-" + Binding.Id.ToString("D") + "-" + Ordinal.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
    public AutomationScript Script { get; }
    public AutomationUser User { get; }
    public AutomationBinding Binding { get; }
    public ImmutableArray<AutomationRoot> Roots { get; }
    public ImmutableArray<ExecutionSelectionRevision> SourceRevisions { get; }
    public ExecutionSelectionDisposition Disposition { get; }
    public string ReasonCode { get; }
    public int SuccessfulRunsToday { get; }

    internal ExecutionSelectionItem(int ordinal, AutomationScript script, AutomationUser user, AutomationBinding binding,
        ImmutableArray<AutomationRoot> roots, ImmutableArray<ExecutionSelectionRevision> revisions,
        ExecutionSelectionDisposition disposition, string reasonCode, int successfulRunsToday)
    {
        Ordinal = ordinal; Script = script; User = user; Binding = binding; Roots = roots;
        SourceRevisions = revisions; Disposition = disposition; ReasonCode = reasonCode;
        SuccessfulRunsToday = successfulRunsToday;
    }
}

public sealed class ExecutionSelection
{
    public ExecutionSelectionRequest Request { get; }
    public long CatalogRevision { get; }
    public long SettingsRevision { get; }
    public AutomationQueue? Queue { get; }
    public AutomationCompletionAction CompletionAction => Queue?.CompletionAction ?? AutomationCompletionAction.None;
    public ImmutableArray<ExecutionSelectionItem> Items { get; }
    public ImmutableArray<ExecutionSelectionRevision> SourceRevisions { get; }
    public bool HasCandidates => Items.Any(item => item.Disposition == ExecutionSelectionDisposition.Candidate);
    public bool HasBlockedItems => Items.Any(item => item.Disposition == ExecutionSelectionDisposition.Blocked);
    public string ReasonCode { get; }

    internal ExecutionSelection(ExecutionSelectionRequest request, long catalogRevision, long settingsRevision, AutomationQueue? queue,
        ImmutableArray<ExecutionSelectionItem> items, ImmutableArray<ExecutionSelectionRevision> revisions, string reasonCode)
    {
        Request = request; CatalogRevision = catalogRevision; SettingsRevision = settingsRevision; Queue = queue;
        Items = items; SourceRevisions = revisions; ReasonCode = reasonCode;
    }
}

public sealed record ExecutionItemReadiness(ExecutionSelectionItem Item, bool IsReady, string ReasonCode);
public sealed record ExecutionItemExplanation(ExecutionSelectionItem Source, ExecutionExplanationDisposition Disposition, string ReasonCode);
public sealed record ExecutionGroupExplanation(Guid ScriptId, ExecutionExplanationDisposition Disposition, string ReasonCode);

public sealed class ExecutionPlanExplanation
{
    public ExecutionSelection Selection { get; }
    public ImmutableArray<ExecutionItemExplanation> Items { get; }
    public ImmutableArray<ExecutionGroupExplanation> Groups { get; }
    public bool HasRunnable => Items.Any(item => item.Disposition == ExecutionExplanationDisposition.Runnable);
    public bool HasBlockedItems => Items.Any(item => item.Disposition == ExecutionExplanationDisposition.Blocked);
    public string ReasonCode { get; }

    internal ExecutionPlanExplanation(ExecutionSelection selection, ImmutableArray<ExecutionItemExplanation> items, string reasonCode)
    {
        Selection = selection; Items = items; ReasonCode = reasonCode;
        Groups = selection.Queue is null ? [] : selection.Queue.ScriptIds.Select((script, index) =>
        {
            var matching = items.Where(item => item.Source.Script.Id == script).ToArray();
            var count = matching.Length / selection.Queue.ScriptIds.Count(id => id == script);
            var prior = selection.Queue.ScriptIds.Take(index).Count(id => id == script);
            var group = matching.Skip(prior * count).Take(count).ToArray();
            if (group.Length == 0) return new ExecutionGroupExplanation(script, ExecutionExplanationDisposition.Skipped, "execution_no_enabled_bindings");
            var disposition = group.Any(item => item.Disposition == ExecutionExplanationDisposition.Blocked) ? ExecutionExplanationDisposition.Blocked
                : group.Any(item => item.Disposition == ExecutionExplanationDisposition.Runnable) ? ExecutionExplanationDisposition.Runnable : ExecutionExplanationDisposition.Skipped;
            var reason = group.All(item => item.Disposition == ExecutionExplanationDisposition.Runnable) ? "execution_inputs_verified"
                : disposition == ExecutionExplanationDisposition.Runnable ? "execution_group_partial" : group.First(item => item.Disposition == disposition).ReasonCode;
            return new(script, disposition, reason);
        }).ToImmutableArray();
    }
}
