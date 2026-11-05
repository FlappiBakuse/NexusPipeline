using System.Collections.Immutable;

namespace NexusPipeline.Core.Automation;

public static class AutomationEdits
{
    public static AutomationCatalog Apply(AutomationCatalog current, AutomationChanges changes)
    {
        AutomationCatalogPolicy.Validate(current);
        if (changes.ExpectedCatalogRevision != current.Revision) throw new AutomationConflictException();
        if (changes.Removed.IsDefault || changes.Removed.Length > 4096 ||
            changes.Removed.Any(item => item is null || !Enum.IsDefined(item.Kind) || item.Id == Guid.Empty || item.ExpectedRevision < 1) ||
            changes.Removed.Select(item => (item.Kind, item.Id)).Distinct().Count() != changes.Removed.Length)
            throw new AutomationDefinitionException("automation_removal_invalid");
        var roots = Merge(current.Roots, changes.Roots, changes.Removed, AutomationDefinitionKind.Root,
            value => value.Id, value => value.Revision, (value, revision) => value with { Revision = revision });
        VerifyScriptIdentity(current.Scripts, changes.Scripts);
        var scripts = Merge(current.Scripts, changes.Scripts, changes.Removed, AutomationDefinitionKind.Script,
            value => value.Id, value => value.Revision, (value, revision) => value with { Revision = revision });
        var users = Merge(current.Users, changes.Users, changes.Removed, AutomationDefinitionKind.User,
            value => value.Id, value => value.Revision, (value, revision) => value with { Revision = revision });
        if (changes.Bindings.IsDefault) throw new AutomationDefinitionException("automation_changes_limit");
        var priorBindings = current.Bindings.ToDictionary(value => value.Id);
        if (changes.Bindings.Any(value => value is not null && priorBindings.TryGetValue(value.Id, out var prior) &&
            (value.UserId != prior.UserId || value.ScriptId != prior.ScriptId)))
            throw new AutomationDefinitionException("automation_binding_identity_changed");
        var bindings = Merge(current.Bindings, changes.Bindings, changes.Removed, AutomationDefinitionKind.Binding,
            value => value.Id, value => value.Revision, (value, revision) => value with { Revision = revision });
        var queues = Merge(current.Queues, changes.Queues, changes.Removed, AutomationDefinitionKind.Queue,
            value => value.Id, value => value.Revision, (value, revision) => value with { Revision = revision });
        var schedules = Merge(current.Schedules, changes.Schedules, changes.Removed, AutomationDefinitionKind.Schedule,
            value => value.Id, value => value.Revision, (value, revision) => value with { Revision = revision });
        var deletedUsers = Removed(changes, AutomationDefinitionKind.User);
        var deletedScripts = Removed(changes, AutomationDefinitionKind.Script);
        if (changes.Bindings.Any(value => deletedUsers.Contains(value.UserId) || deletedScripts.Contains(value.ScriptId)))
            throw new AutomationDefinitionException("automation_deletion_reference_conflict");
        bindings = bindings.Where(value => !deletedUsers.Contains(value.UserId) && !deletedScripts.Contains(value.ScriptId)).ToImmutableArray();
        var priorQueues = current.Queues.ToDictionary(value => value.Id);
        queues = queues.Select(queue => RemoveDeadItems(queue, deletedScripts, priorQueues)).OrderBy(queue => queue.Order).ThenBy(queue => queue.Id).ToImmutableArray();
        var deletedQueues = Removed(changes, AutomationDefinitionKind.Queue);
        if (changes.Schedules.Any(value => deletedQueues.Contains(value.QueueId)))
            throw new AutomationDefinitionException("automation_deletion_reference_conflict");
        schedules = schedules.Where(value => !deletedQueues.Contains(value.QueueId)).ToImmutableArray();
        var next = new AutomationCatalog(checked(current.Revision + 1), roots, scripts, users, bindings, queues, schedules);
        AutomationCatalogPolicy.Validate(next);
        return next;
    }

    private static void VerifyScriptIdentity(ImmutableArray<AutomationScript> current, ImmutableArray<AutomationScript> saved)
    {
        if (saved.IsDefault) throw new AutomationDefinitionException("automation_changes_limit");
        var prior = current.ToDictionary(value => value.Id);
        if (saved.Any(value => value is not null && prior.TryGetValue(value.Id, out var original) &&
            (value.Kind != original.Kind || value.ExtensionId != original.ExtensionId)))
            throw new AutomationDefinitionException("automation_script_identity_changed");
    }

    private static AutomationQueue RemoveDeadItems(AutomationQueue queue, HashSet<Guid> deletedScripts, Dictionary<Guid, AutomationQueue> prior)
    {
        if (queue.ScriptIds.IsDefault) throw new AutomationDefinitionException("automation_queue_invalid");
        var remaining = queue.ScriptIds.Where(id => !deletedScripts.Contains(id)).ToImmutableArray();
        if (remaining.Length == queue.ScriptIds.Length) return queue;
        var dependencies = queue.SuccessDependencies.Where(item => !deletedScripts.Contains(item.ScriptId))
            .Select(item => item with { RequiredScriptIds = item.RequiredScriptIds.Where(id => !deletedScripts.Contains(id)).ToImmutableArray() })
            .Where(item => !item.RequiredScriptIds.IsEmpty).ToImmutableArray();
        return queue with { ScriptIds = remaining, SuccessDependencies = dependencies, Revision = prior.TryGetValue(queue.Id, out var old) ? checked(old.Revision + 1) : 1 };
    }

    private static HashSet<Guid> Removed(AutomationChanges changes, AutomationDefinitionKind kind) =>
        changes.Removed.Where(value => value.Kind == kind).Select(value => value.Id).ToHashSet();

    private static ImmutableArray<T> Merge<T>(ImmutableArray<T> current, ImmutableArray<T> saved,
        ImmutableArray<RemoveAutomationDefinition> removed, AutomationDefinitionKind kind,
        Func<T, Guid> id, Func<T, long> revision, Func<T, long, T> advance)
    {
        if (saved.IsDefault || saved.Length > 4096) throw new AutomationDefinitionException("automation_changes_limit");
        var next = current.ToDictionary(id);
        var seen = new HashSet<Guid>();
        foreach (var value in saved)
        {
            if (value is null || id(value) == Guid.Empty || !seen.Add(id(value)))
                throw new AutomationDefinitionException("automation_changes_identity_invalid");
            var expected = next.TryGetValue(id(value), out var prior) ? revision(prior) : 0;
            if (revision(value) != expected) throw new AutomationConflictException();
            next[id(value)] = advance(value, checked(expected + 1));
        }
        foreach (var deletion in removed.Where(value => value.Kind == kind))
        {
            if (seen.Contains(deletion.Id)) throw new AutomationDefinitionException("automation_save_and_remove_conflict");
            if (!next.TryGetValue(deletion.Id, out var value) || revision(value) != deletion.ExpectedRevision)
                throw new AutomationConflictException();
            next.Remove(deletion.Id);
        }
        return next.Values.OrderBy(id).ToImmutableArray();
    }
}
