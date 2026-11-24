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
        Groups = selection.Queue is { } queue ? DescribeGroups(queue, items) : [];
    }

    private static ImmutableArray<ExecutionGroupExplanation> DescribeGroups(AutomationQueue queue, ImmutableArray<ExecutionItemExplanation> items)
    {
        var repetitions = queue.ScriptIds.GroupBy(id => id).ToDictionary(group => group.Key, group => group.Count());
        var members = items.GroupBy(item => item.Source.Script.Id).ToDictionary(group => group.Key, group => group.ToArray());
        var offsets = new Dictionary<Guid, int>();
        var groups = ImmutableArray.CreateBuilder<ExecutionGroupExplanation>(queue.ScriptIds.Length);
        foreach (var script in queue.ScriptIds)
        {
            var group = members.GetValueOrDefault(script);
            var count = (group?.Length ?? 0) / repetitions[script];
            var offset = offsets.GetValueOrDefault(script);
            groups.Add(DescribeGroup(script, group.AsSpan(offset, count)));
            offsets[script] = offset + count;
        }
        return groups.MoveToImmutable();
    }

    private static ExecutionGroupExplanation DescribeGroup(Guid script, ReadOnlySpan<ExecutionItemExplanation> group)
    {
        if (group.IsEmpty) return new(script, ExecutionExplanationDisposition.Skipped, "execution_no_enabled_bindings");
        var runnable = 0;
        foreach (var item in group)
        {
            if (item.Disposition == ExecutionExplanationDisposition.Blocked)
                return new(script, ExecutionExplanationDisposition.Blocked, item.ReasonCode);
            if (item.Disposition == ExecutionExplanationDisposition.Runnable) runnable++;
        }
        return runnable == 0 ? new(script, ExecutionExplanationDisposition.Skipped, group[0].ReasonCode) :
            new(script, ExecutionExplanationDisposition.Runnable,
                runnable == group.Length ? ExecutionSelectionPolicy.InputsVerified : "execution_group_partial");
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
        var roots = catalog.Roots.ToDictionary(item => item.Id);
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
                sources.AddRange(queueRoots.Select(id => roots.GetValueOrDefault(id)).OfType<AutomationRoot>().Select(root =>
                    new ExecutionSelectionRevision(AutomationDefinitionKind.Root, root.Id, root.Revision)));
                break;
        }
        IEnumerable<(AutomationBinding Source, AutomationBinding Effective)> resolved;
        if (queue is null) resolved = selected.Select(ResolveBinding);
        else
        {
            var queueScripts = queue.ScriptIds.ToHashSet();
            var byScript = catalog.Bindings.Where(binding => queueScripts.Contains(binding.ScriptId)).Select(ResolveBinding)
                .Where(binding => binding.Effective.IsEnabled)
                .OrderBy(binding => users[binding.Source.UserId].Order)
                .ThenBy(binding => binding.Source.UserId.ToString("D"), StringComparer.Ordinal)
                .ToLookup(binding => binding.Source.ScriptId);
            resolved = queue.ScriptIds.SelectMany(id => byScript[id]);
        }
        var items = resolved.Select((binding, ordinal) => Item(roots, settings,
            BindingScriptSource.Resolve(scripts[binding.Source.ScriptId], binding.Source), users[binding.Source.UserId],
            binding.Effective, ordinal, successfulRunsToday?.GetValueOrDefault(binding.Source.Id) ?? 0,
            request.Target is ProjectInitializationTarget)).ToImmutableArray();
        if (queue is { SuccessDependencies.IsEmpty: false })
        {
            var dependencies = queue.SuccessDependencies.ToDictionary(item => item.ScriptId, item => item.RequiredScriptIds);
            var prior = new Dictionary<Guid, bool>();
            var projected = ImmutableArray.CreateBuilder<ExecutionSelectionItem>(items.Length);
            foreach (var item in items)
            {
                var script = item.Script.Id;
                var current = item;
                if (item.Disposition == ExecutionSelectionDisposition.Candidate && HasUnsuccessfulDependency(dependencies, prior, script))
                    current = new(item.Ordinal, item.Script, item.User, item.Binding, item.Roots, item.SourceRevisions,
                        ExecutionSelectionDisposition.Skipped, "execution_queue_dependency_not_successful", item.SuccessfulRunsToday);
                prior[script] = prior.GetValueOrDefault(script, true) && current.Disposition == ExecutionSelectionDisposition.Candidate;
                projected.Add(current);
            }
            items = projected.MoveToImmutable();
        }
        var reason = unavailable ?? (items.Any(item => item.Disposition == ExecutionSelectionDisposition.Candidate) ? "execution_inputs_required" :
            items.Any(item => item.Disposition == ExecutionSelectionDisposition.Blocked) ? "execution_no_runnable_targets" :
            queue is not null && queue.ScriptIds.IsEmpty ? "execution_queue_empty" :
            items.Length > 0 && items.All(item => item.ReasonCode == "execution_daily_success_limit")
                ? "execution_daily_success_limit" : "execution_no_enabled_bindings");
        return new(request, catalog.Revision, settings.Revision, queue, items,
            Revisions(sources.Concat(items.SelectMany(item => item.SourceRevisions))), reason);

        (AutomationBinding Source, AutomationBinding Effective) ResolveBinding(AutomationBinding binding)
            => (binding, AutomationBindingPolicy.Resolve(users[binding.UserId], binding));
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
        if (selection.Queue is { SuccessDependencies.IsEmpty: false } queue)
        {
            var dependencies = queue.SuccessDependencies.ToDictionary(item => item.ScriptId, item => item.RequiredScriptIds);
            var prior = new Dictionary<Guid, bool>();
            var projected = ImmutableArray.CreateBuilder<ExecutionItemExplanation>(items.Length);
            foreach (var item in items)
            {
                var script = item.Source.Script.Id;
                var current = item;
                if (item.Disposition == ExecutionExplanationDisposition.Runnable && HasUnsuccessfulDependency(dependencies, prior, script))
                    current = item with { Disposition = ExecutionExplanationDisposition.Skipped, ReasonCode = "execution_queue_dependency_not_successful" };
                prior[script] = prior.GetValueOrDefault(script, true) && current.Disposition == ExecutionExplanationDisposition.Runnable;
                projected.Add(current);
            }
            items = projected.MoveToImmutable();
        }
        var reason = items.Any(item => item.Disposition == ExecutionExplanationDisposition.Runnable) ? "execution_has_runnable_items" :
            items.Any(item => item.Disposition == ExecutionExplanationDisposition.Blocked) ? "execution_no_runnable_targets" : selection.ReasonCode;
        return new(selection, items, reason);
    }

    private static bool HasUnsuccessfulDependency(IReadOnlyDictionary<Guid, ImmutableArray<Guid>> dependencies,
        IReadOnlyDictionary<Guid, bool> prior, Guid script)
        => dependencies.TryGetValue(script, out var required) && required.Any(id => !prior.GetValueOrDefault(id));

    private static ExecutionSelectionItem Item(IReadOnlyDictionary<Guid, AutomationRoot> rootsById, HostSettingsSnapshot settings,
        AutomationScript original, AutomationUser user, AutomationBinding binding, int ordinal, int successesToday, bool initialize)
    {
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
        user = user with { };
        var frozenBinding = binding with { PreHook = FreezeHook(binding.PreHook), PostHook = FreezeHook(binding.PostHook) };
        var hookPaths = new[] { frozenBinding.PreHook, frozenBinding.PostHook }.OfType<AutomationHook>().Select(hook => hook.Executable);
        var rootIds = script.ConfigurationPaths.Concat(script.LogPaths).Append(script.Executable).Concat(hookPaths).Concat(AutomationTargetPolicy.Paths(script.Target))
            .Select(path => path.RootId).Append(script.RootId).ToHashSet();
        var roots = rootIds.Select(id => rootsById.GetValueOrDefault(id)).OfType<AutomationRoot>()
            .OrderBy(root => root.Id.ToString("D"), StringComparer.Ordinal)
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
