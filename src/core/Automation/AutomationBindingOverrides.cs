using System.Collections.Immutable;

namespace NexusPipeline.Core.Automation;

public sealed record AutomationGeneralOverride(bool SyncEnabled, bool IsEnabled, int RunDays, int MaximumSuccessfulRunsPerDay);
public sealed record AutomationNotificationOverride(bool SyncEnabled, bool NotifyEnabled, ImmutableArray<Guid> NotificationTaskIds);
public sealed record AutomationAdvancedOverride(bool SyncEnabled, AutomationHook? PreHook, AutomationHook? PostHook,
    bool PreOnceAfterSuccess, bool PostFinalOnly);
public sealed record AutomationBindingOverrides
{
    public AutomationGeneralOverride General { get; init; } = new(false, true, -1, -1);
    public AutomationNotificationOverride Notification { get; init; } = new(false, true, []);
    public AutomationAdvancedOverride Advanced { get; init; } = new(false, null, null, false, false);
}
public sealed record AutomationQueueDependency(Guid ScriptId, ImmutableArray<Guid> RequiredScriptIds);

public static class AutomationBindingPolicy
{
    public static AutomationBinding Resolve(AutomationUser user, AutomationBinding raw)
    {
        var overrides = user.BindingOverrides;
        var result = raw with { };
        if (overrides.General.SyncEnabled)
            result = result with { IsEnabled = overrides.General.IsEnabled, RunDays = overrides.General.RunDays,
                MaximumSuccessfulRunsPerDay = overrides.General.MaximumSuccessfulRunsPerDay };
        if (overrides.Notification.SyncEnabled)
            result = result with { NotifyEnabled = overrides.Notification.NotifyEnabled,
                NotificationTaskIds = overrides.Notification.NotificationTaskIds };
        if (overrides.Advanced.SyncEnabled)
            result = result with { PreHook = overrides.Advanced.PreHook, PostHook = overrides.Advanced.PostHook,
                PreOnceAfterSuccess = overrides.Advanced.PreOnceAfterSuccess, PostFinalOnly = overrides.Advanced.PostFinalOnly };
        return result;
    }

    public static bool ValidRunDays(int value) => value is >= -1 and <= 36500;
    public static bool ValidDailyLimit(int value) => value == -1 || value is >= 1 and <= 9999;
}
