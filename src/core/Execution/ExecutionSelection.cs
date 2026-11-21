using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Settings;

namespace NexusPipeline.Core.Execution;

public abstract record ExecutionTarget;
public sealed record BindingTarget(Guid BindingId) : ExecutionTarget;
public sealed record ProjectInitializationTarget(Guid BindingId) : ExecutionTarget;
public sealed record ScriptTarget(Guid ScriptId) : ExecutionTarget;
public sealed record ScriptUserTarget(Guid ScriptId, Guid UserId) : ExecutionTarget;
public sealed record QueueTarget(Guid QueueId) : ExecutionTarget;

public sealed record ExecutionSelectionRequest(ExecutionTarget Target, long ExpectedCatalogRevision, long ExpectedSettingsRevision);
public sealed class ExecutionSelectionException(string code) : Exception(code);

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

public static class ExecutionSelectionPolicy
{
    public const string InputsVerified = "execution_inputs_verified";

    public static ExecutionSelection Select(AutomationCatalog catalog, HostSettingsSnapshot settings, ExecutionSelectionRequest request, IReadOnlyDictionary<Guid, int>? successfulRunsToday = null)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(settings);
        request = FreezeRequest(request);
        AutomationCatalogPolicy.Validate(catalog); HostSettingsPolicy.Validate(settings.Values);
        if (catalog.Revision != request.ExpectedCatalogRevision) throw new ExecutionSelectionException("execution_catalog_revision_conflict");
        if (settings.Revision != request.ExpectedSettingsRevision) throw new ExecutionSelectionException("execution_settings_revision_conflict");
        var bindings = catalog.Bindings.ToDictionary(item => item.Id);
        var users = catalog.Users.ToDictionary(item => item.Id);
        var scripts = catalog.Scripts.ToDictionary(item => item.Id);
        var sources = new List<ExecutionSelectionRevision>();
        AutomationQueue? queue = null;
        string? unavailable = null;
        IEnumerable<AutomationBinding> selected = [];
        switch (request.Target)
        {
            case ProjectInitializationTarget target:
                if (bindings.TryGetValue(target.BindingId, out var initialization)) selected = [initialization];
                else unavailable = "execution_binding_missing";
                break;
            case BindingTarget target:
                if (bindings.TryGetValue(target.BindingId, out var binding)) selected = [binding];
                else unavailable = "execution_binding_missing";
                break;
            case ScriptTarget target:
                if (!scripts.TryGetValue(target.ScriptId, out var script)) { unavailable = "execution_script_missing"; break; }
                sources.Add(new(AutomationDefinitionKind.Script, script.Id, script.Revision));
                selected = catalog.Bindings.Where(item => item.ScriptId == script.Id).OrderBy(item => users[item.UserId].Order)
                    .ThenBy(item => item.UserId.ToString("D"), StringComparer.Ordinal);
                break;
            case ScriptUserTarget target:
                if (!scripts.TryGetValue(target.ScriptId, out var userScript)) { unavailable = "execution_script_missing"; break; }
                if (!users.TryGetValue(target.UserId, out var user)) { unavailable = "execution_user_missing"; break; }
                sources.Add(new(AutomationDefinitionKind.Script, userScript.Id, userScript.Revision));
                sources.Add(new(AutomationDefinitionKind.User, user.Id, user.Revision));
                selected = catalog.Bindings.Where(item => item.ScriptId == target.ScriptId && item.UserId == target.UserId);
                break;
            case QueueTarget target:
                queue = catalog.Queues.SingleOrDefault(item => item.Id == target.QueueId);
                if (queue is null) { unavailable = "execution_queue_missing"; break; }
                sources.Add(new(AutomationDefinitionKind.Queue, queue.Id, queue.Revision));
                queue = queue with { ScriptIds = queue.ScriptIds.Select(id => id).ToImmutableArray() };
                sources.AddRange(queue.ScriptIds.Select(id => new ExecutionSelectionRevision(AutomationDefinitionKind.Script, id, scripts[id].Revision)));
                var queueRoots = queue.ScriptIds.Select(id => scripts[id]).SelectMany(value => value.ConfigurationPaths.Concat(value.LogPaths)
                    .Append(value.Executable).Concat(AutomationTargetPolicy.Paths(value.Target)).Select(path => path.RootId).Append(value.RootId)).ToHashSet();
                sources.AddRange(catalog.Roots.Where(root => queueRoots.Contains(root.Id)).Select(root =>
                    new ExecutionSelectionRevision(AutomationDefinitionKind.Root, root.Id, root.Revision)));
                selected = queue.ScriptIds.SelectMany(id => catalog.Bindings.Where(binding => binding.ScriptId == id &&
                    AutomationBindingPolicy.Resolve(users[binding.UserId], binding).IsEnabled)
                    .OrderBy(binding => users[binding.UserId].Order).ThenBy(binding => binding.UserId.ToString("D"), StringComparer.Ordinal));
                break;
        }
        var items = selected.Select((binding, ordinal) => Item(catalog, settings, binding, ordinal, successfulRunsToday?.GetValueOrDefault(binding.Id) ?? 0, request.Target is ProjectInitializationTarget)).ToImmutableArray();
        if (queue is not null)
        {
            var projected = items.ToArray();
            foreach (var item in items.Where(item => item.Disposition == ExecutionSelectionDisposition.Candidate))
            {
                var required = queue.SuccessDependencies.SingleOrDefault(value => value.ScriptId == item.Script.Id)?.RequiredScriptIds ?? [];
                if (required.Any(id => !projected.Any(prior => prior.Script.Id == id && prior.Ordinal < item.Ordinal) ||
                    projected.Any(prior => prior.Script.Id == id && prior.Ordinal < item.Ordinal && prior.Disposition != ExecutionSelectionDisposition.Candidate)))
                    projected[item.Ordinal] = new(item.Ordinal, item.Script, item.User, item.Binding, item.Roots, item.SourceRevisions,
                        ExecutionSelectionDisposition.Skipped, "execution_queue_dependency_not_successful", item.SuccessfulRunsToday);
            }
            items = projected.ToImmutableArray();
        }
        var reason = unavailable ?? (items.Any(item => item.Disposition == ExecutionSelectionDisposition.Candidate) ? "execution_inputs_required" :
            items.Any(item => item.Disposition == ExecutionSelectionDisposition.Blocked) ? "execution_no_runnable_targets" :
            queue is not null && queue.ScriptIds.IsEmpty ? "execution_queue_empty" :
            items.Length > 0 && items.All(item => item.ReasonCode == "execution_daily_success_limit")
                ? "execution_daily_success_limit" : "execution_no_enabled_bindings");
        return new(request, catalog.Revision, settings.Revision, queue, items,
            Revisions(sources.Concat(items.SelectMany(item => item.SourceRevisions))), reason);
    }

    public static ExecutionPlanExplanation Explain(ExecutionSelection selection, ImmutableArray<ExecutionItemReadiness> readiness, bool blockAll = false)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (readiness.IsDefault || readiness.Length != selection.Items.Count(item => item.Disposition == ExecutionSelectionDisposition.Candidate))
            throw new ExecutionSelectionException("execution_readiness_unbound");
        var byOrdinal = new Dictionary<int, ExecutionItemReadiness>();
        foreach (var result in readiness)
        {
            if (result is null || result.Item is null || result.Item.Ordinal < 0 || result.Item.Ordinal >= selection.Items.Length ||
                !ReferenceEquals(result.Item, selection.Items[result.Item.Ordinal]) || result.Item.Disposition != ExecutionSelectionDisposition.Candidate ||
                !byOrdinal.TryAdd(result.Item.Ordinal, result) || !Reason(result.ReasonCode) ||
                result.IsReady != (result.ReasonCode == InputsVerified))
                throw new ExecutionSelectionException("execution_readiness_unbound");
        }
        var items = selection.Items.Select(item => item.Disposition switch
        {
            ExecutionSelectionDisposition.Skipped => new ExecutionItemExplanation(item, ExecutionExplanationDisposition.Skipped, item.ReasonCode),
            ExecutionSelectionDisposition.Blocked => new ExecutionItemExplanation(item, ExecutionExplanationDisposition.Blocked, item.ReasonCode),
            _ => new ExecutionItemExplanation(item, byOrdinal[item.Ordinal].IsReady ? ExecutionExplanationDisposition.Runnable :
                selection.Queue is null || blockAll ? ExecutionExplanationDisposition.Blocked : ExecutionExplanationDisposition.Skipped, byOrdinal[item.Ordinal].ReasonCode)
        }).ToImmutableArray();
        if (selection.Queue is { } queue)
        {
            var projected = items.ToArray();
            foreach (var item in items.Where(item => item.Disposition == ExecutionExplanationDisposition.Runnable))
            {
                var required = queue.SuccessDependencies.SingleOrDefault(value => value.ScriptId == item.Source.Script.Id)?.RequiredScriptIds ?? [];
                if (required.Any(id => !projected.Any(prior => prior.Source.Script.Id == id && prior.Source.Ordinal < item.Source.Ordinal) ||
                    projected.Any(prior => prior.Source.Script.Id == id && prior.Source.Ordinal < item.Source.Ordinal && prior.Disposition != ExecutionExplanationDisposition.Runnable)))
                    projected[item.Source.Ordinal] = item with { Disposition = ExecutionExplanationDisposition.Skipped, ReasonCode = "execution_queue_dependency_not_successful" };
            }
            items = projected.ToImmutableArray();
        }
        var reason = items.Any(item => item.Disposition == ExecutionExplanationDisposition.Runnable) ? "execution_has_runnable_items" :
            items.Any(item => item.Disposition == ExecutionExplanationDisposition.Blocked) ? "execution_no_runnable_targets" : selection.ReasonCode;
        return new(selection, items, reason);
    }

    private static ExecutionSelectionItem Item(AutomationCatalog catalog, HostSettingsSnapshot settings, AutomationBinding binding, int ordinal, int successesToday, bool initialize)
    {
        var original = BindingScriptSource.Resolve(catalog.Scripts.Single(item => item.Id == binding.ScriptId), binding);
        var script = original with
        {
            Target = AutomationTargetPolicy.Freeze(original.Target),
            Arguments = original.Arguments.Select(value => value).ToImmutableArray(),
            AdapterResources = original.AdapterResources.Select(resource => resource with { Access = ConfigurationPatchPolicy.Freeze(resource.Access) }).ToImmutableArray(),
            ConfigurationPaths = original.ConfigurationPaths.Select(path => path with { }).ToImmutableArray(),
            LogPaths = original.LogPaths.Select(path => path with { }).ToImmutableArray(),
            Judgement = original.Judgement with
            {
                SuccessKeywords = original.Judgement.SuccessKeywords.Select(value => value).ToImmutableArray(),
                FailureKeywords = original.Judgement.FailureKeywords.Select(value => value).ToImmutableArray()
            }
        };
        var user = catalog.Users.Single(item => item.Id == binding.UserId) with { };
        binding = AutomationBindingPolicy.Resolve(user, binding);
        var frozenBinding = binding with { PreHook = FreezeHook(binding.PreHook), PostHook = FreezeHook(binding.PostHook) };
        var hookPaths = new[] { frozenBinding.PreHook, frozenBinding.PostHook }.OfType<AutomationHook>().Select(hook => hook.Executable);
        var rootIds = script.ConfigurationPaths.Concat(script.LogPaths).Append(script.Executable).Concat(hookPaths).Concat(AutomationTargetPolicy.Paths(script.Target))
            .Select(path => path.RootId).Append(script.RootId).ToHashSet();
        var roots = catalog.Roots.Where(root => rootIds.Contains(root.Id)).OrderBy(root => root.Id.ToString("D"), StringComparer.Ordinal)
            .Select(root => root with { }).ToImmutableArray();
        var revisions = Revisions(roots.Select(root => new ExecutionSelectionRevision(AutomationDefinitionKind.Root, root.Id, root.Revision)).Concat([
            new(AutomationDefinitionKind.Script, script.Id, script.Revision), new(AutomationDefinitionKind.User, user.Id, user.Revision),
            new(AutomationDefinitionKind.Binding, binding.Id, binding.Revision)]));
        var runDaysExhausted = !initialize && binding.RunDays == 0;
        var dailyLimit = !initialize && binding.MaximumSuccessfulRunsPerDay > 0 && successesToday >= binding.MaximumSuccessfulRunsPerDay;
        var disabledExtension = script.Kind == AutomationKind.Specialized &&
            settings.Values.Extensions.Any(preference => preference.ExtensionId == script.ExtensionId && !preference.Enabled);
        var disposition = !binding.IsEnabled || runDaysExhausted || dailyLimit || disabledExtension
            ? ExecutionSelectionDisposition.Skipped : ExecutionSelectionDisposition.Candidate;
        var reason = disposition switch
        {
            ExecutionSelectionDisposition.Skipped => !binding.IsEnabled ? "execution_binding_disabled" : runDaysExhausted
                ? "execution_run_days_exhausted" : dailyLimit ? "execution_daily_success_limit" : "execution_extension_disabled",
            _ => "execution_inputs_required"
        };
        return new(ordinal, script, user, frozenBinding, roots, revisions, disposition, reason, successesToday);
    }

    private static AutomationHook? FreezeHook(AutomationHook? hook) => hook is null ? null :
        new(hook.Executable with { }, hook.Arguments.Select(value => value).ToImmutableArray());

    public static ExecutionSelectionRequest FreezeRequest(ExecutionSelectionRequest request, IReadOnlyDictionary<Guid, int>? successfulRunsToday = null)
    {
        if (request is null || request.ExpectedCatalogRevision is < 0 or > 9007199254740991 || request.ExpectedSettingsRevision is < 0 or > 9007199254740991)
            throw new ExecutionSelectionException("execution_selection_request_invalid");
        ExecutionTarget target = request.Target switch
        {
            BindingTarget value when value.BindingId != Guid.Empty => value with { },
            ProjectInitializationTarget value when value.BindingId != Guid.Empty => value with { },
            ScriptTarget value when value.ScriptId != Guid.Empty => value with { },
            ScriptUserTarget value when value.ScriptId != Guid.Empty && value.UserId != Guid.Empty => value with { },
            QueueTarget value when value.QueueId != Guid.Empty => value with { },
            _ => throw new ExecutionSelectionException("execution_selection_request_invalid")
        };
        return request with { Target = target };
    }

    private static ImmutableArray<ExecutionSelectionRevision> Revisions(IEnumerable<ExecutionSelectionRevision> revisions) => revisions.Distinct()
        .OrderBy(item => item.Kind).ThenBy(item => item.EntityId.ToString("D"), StringComparer.Ordinal).ToImmutableArray();

    private static bool Reason(string reason) => reason is { Length: > 0 and <= 80 } &&
        reason.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}
