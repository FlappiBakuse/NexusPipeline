using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using NexusPipeline.Core.Extensions;

namespace NexusPipeline.Core.Automation;

public static class AutomationCatalogPolicy
{
    public static void Validate(AutomationCatalog catalog)
    {
        if (catalog.Revision is < 0 or > 9007199254740991) Reject("automation_catalog_revision_invalid");
        var roots = Unique(catalog.Roots, value => value.Id, value => value.Revision, 128);
        var scripts = Unique(catalog.Scripts, value => value.Id, value => value.Revision, 4096);
        var users = Unique(catalog.Users, value => value.Id, value => value.Revision, 4096);
        var bindings = Unique(catalog.Bindings, value => value.Id, value => value.Revision, 4096);
        var queues = Unique(catalog.Queues, value => value.Id, value => value.Revision, 4096);
        _ = Unique(catalog.Schedules, value => value.Id, value => value.Revision, 4096);
        foreach (var root in catalog.Roots)
            if (!Name(root.Name) || !Enum.IsDefined(root.Kind) || string.IsNullOrWhiteSpace(root.Path)) Reject("automation_root_invalid");
        foreach (var script in catalog.Scripts) ValidateScript(script, roots);
        foreach (var user in catalog.Users)
        {
            if (!Name(user.Name) || user.Order is < 0 or > 4095 || user.Notes is null || user.Notes.Length > 4000 ||
                user.Notes.Contains('\0') || user.Avatar is null || user.Avatar.Length > 65536 ||
                user.Avatar.Length > 0 && !user.Avatar.StartsWith("data:image/png;base64,", StringComparison.Ordinal) || user.BindingOverrides is null ||
                user.BindingOverrides.General is null || user.BindingOverrides.Notification is null || user.BindingOverrides.Advanced is null)
                Reject("automation_user_invalid");
            var overrides = user.BindingOverrides;
            if (!AutomationBindingPolicy.ValidRunDays(overrides.General.RunDays) ||
                !AutomationBindingPolicy.ValidDailyLimit(overrides.General.MaximumSuccessfulRunsPerDay)) Reject("automation_binding_limits_invalid");
            NexusPipeline.Core.Notifications.NotificationRoutingPolicy.RequireSelection(overrides.Notification.NotificationTaskIds);
            ValidateHook(overrides.Advanced.PreHook, roots); ValidateHook(overrides.Advanced.PostHook, roots);
            if (overrides.Advanced.PreHook is null && overrides.Advanced.PreOnceAfterSuccess ||
                overrides.Advanced.PostHook is null && overrides.Advanced.PostFinalOnly) Reject("automation_hook_policy_unbound");
        }
        var pairs = new HashSet<(Guid User, Guid Script)>();
        foreach (var binding in catalog.Bindings)
        {
            if (!users.Contains(binding.UserId) || !scripts.Contains(binding.ScriptId) || binding.Priority is < 0 or > 9999 ||
                !pairs.Add((binding.UserId, binding.ScriptId))) Reject("automation_binding_invalid");
            if (!AutomationBindingPolicy.ValidRunDays(binding.RunDays) ||
                !AutomationBindingPolicy.ValidDailyLimit(binding.MaximumSuccessfulRunsPerDay)) Reject("automation_binding_limits_invalid");
            NexusPipeline.Core.Notifications.NotificationRoutingPolicy.RequireSelection(binding.NotificationTaskIds);
            ValidateHook(binding.PreHook, roots); ValidateHook(binding.PostHook, roots);
            if (binding.PreHook is null && binding.PreOnceAfterSuccess || binding.PostHook is null && binding.PostFinalOnly)
                Reject("automation_hook_policy_unbound");
        }
        foreach (var queue in catalog.Queues)
        {
            NexusPipeline.Core.Notifications.NotificationRoutingPolicy.RequireSelection(queue.NotificationTaskIds);
            if (!Name(queue.Name) || !Enum.IsDefined(queue.CompletionAction) || queue.Order is < 0 or > 4095 ||
                queue.ScriptIds.IsDefault || queue.ScriptIds.Length > 4096 || queue.ScriptIds.Any(id => !scripts.Contains(id)))
                Reject("automation_queue_invalid");
            if (queue.SuccessDependencies.IsDefault || queue.SuccessDependencies.Length > queue.ScriptIds.Length ||
                queue.SuccessDependencies.Select(item => item.ScriptId).Distinct().Count() != queue.SuccessDependencies.Length)
                Reject("automation_queue_dependencies_invalid");
            foreach (var dependency in queue.SuccessDependencies)
            {
                var ordinal = queue.ScriptIds.IndexOf(dependency.ScriptId);
                if (ordinal < 0 || dependency.RequiredScriptIds.IsDefaultOrEmpty ||
                    dependency.RequiredScriptIds.Distinct().Count() != dependency.RequiredScriptIds.Length ||
                    dependency.RequiredScriptIds.Any(id => queue.ScriptIds.IndexOf(id) is var prior && (prior < 0 || prior >= ordinal)))
                    Reject("automation_queue_dependencies_invalid");
            }
        }
        foreach (var schedule in catalog.Schedules)
            if (schedule.Order is < 0 or > 4095 || !queues.Contains(schedule.QueueId) || string.IsNullOrWhiteSpace(schedule.TimeZoneId) || schedule.TimeZoneId.Length > 128 ||
                schedule.LocalMinute is < 0 or > 1439 || schedule.Weekdays.IsDefault || schedule.Weekdays.Length is < 1 or > 7 ||
                schedule.Weekdays.Any(day => day is < 0 or > 6) || schedule.Weekdays.Distinct().Count() != schedule.Weekdays.Length)
                Reject("automation_schedule_invalid");
    }

    private static void ValidateScript(AutomationScript script, HashSet<Guid> roots)
    {
        if (script.Order is < 0 or > 4095 || !Name(script.Name) || !Enum.IsDefined(script.Kind) || !roots.Contains(script.RootId) || script.Executable is null ||
            script.Executable.RootId != script.RootId ||
            script.Kind == AutomationKind.Generic && script.ExtensionId is not null)
            Reject("automation_script_invalid");
        if (script.Kind == AutomationKind.Specialized)
        {
            if (!script.UpdateConfigurationAtFinish) Reject("automation_specialized_finish_sync_required");
            try { ExtensionIdentifiers.Require(script.ExtensionId ?? ""); }
            catch (ArgumentException) { Reject("automation_extension_invalid"); }
        }
        if (script.ConfigurationPaths.IsDefault || script.ConfigurationPaths.Length > 64 || script.LogPaths.IsDefault || script.LogPaths.Length > 16 ||
            script.Arguments.IsDefault || script.Arguments.Length > 128 || script.Arguments.Any(value => value is null || value.Length > 4096 || value.Contains('\0')))
            Reject("automation_script_arguments_invalid");
        AutomationAdapterPolicy.Validate(script);
        var paths = script.ConfigurationPaths.AddRange(script.LogPaths).Insert(0, script.Executable!);
        foreach (var path in paths)
            if (path is null || !roots.Contains(path.RootId) || !RelativePath(path.RelativePath)) Reject("automation_portable_path_invalid");
        if (script.Limits is null || script.Limits.MaximumAttempts is < 1 or > 99 || script.Limits.StallTimeoutSeconds is < 0 or > 86400 ||
            script.Limits.TotalTimeoutSeconds is < 0 or > 604800) Reject("automation_limits_invalid");
        AutomationTargetPolicy.Validate(script.Target, roots);
        var judgement = script.Judgement;
        if (judgement is null || !Enum.IsDefined(judgement.Language) || !Keywords(judgement.SuccessKeywords) || !Keywords(judgement.FailureKeywords) ||
            judgement.Script?.Length > 65536 || judgement.Language != AutomationJudgementLanguage.Keywords && string.IsNullOrWhiteSpace(judgement.Script))
            Reject("automation_judgement_invalid");
    }

    private static void ValidateHook(AutomationHook? hook, HashSet<Guid> roots)
    {
        if (hook is null) return;
        if (hook.Executable is null || !roots.Contains(hook.Executable.RootId) || !RelativePath(hook.Executable.RelativePath) ||
            hook.Executable.RelativePath.IndexOfAny(['*', '?', '{', '}']) >= 0 ||
            hook.Arguments.IsDefault || hook.Arguments.Length > 128 ||
            hook.Arguments.Any(value => value is null || value.Length > 4096 || value.Contains('\0')))
            Reject("automation_hook_invalid");
        var extension = hook.Executable.RelativePath[(hook.Executable.RelativePath.LastIndexOf('.') + 1)..].ToLowerInvariant();
        if (extension is not ("exe" or "bat" or "cmd" or "com" or "ps1")) Reject("automation_hook_extension_unsupported");
    }

    public static bool RelativePath(string path) => !string.IsNullOrEmpty(path) && path.Length <= 1024 &&
        !path.Any(value => char.IsControl(value) || value is ':' or '\\') &&
        path.Split('/').All(part => part.Length > 0 && part is not ("." or ".."));

    private static bool Name(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 120 && !name.Contains('\0');
    private static bool Keywords(ImmutableArray<string> values) => !values.IsDefault && values.Length <= 64 &&
        values.All(value => !string.IsNullOrEmpty(value) && value.Length <= 4096 && !value.Contains('\0'));

    private static HashSet<Guid> Unique<T>(ImmutableArray<T> values, Func<T, Guid> id, Func<T, long> revision, int maximum)
    {
        if (values.IsDefault || values.Length > maximum) Reject("automation_definition_limit");
        var result = new HashSet<Guid>();
        foreach (var value in values)
            if (value is null || id(value) == Guid.Empty || revision(value) is < 1 or > 9007199254740991 || !result.Add(id(value)))
                Reject("automation_definition_identity_invalid");
        return result;
    }

    [DoesNotReturn]
    private static void Reject(string code) => throw new AutomationDefinitionException(code);
}

