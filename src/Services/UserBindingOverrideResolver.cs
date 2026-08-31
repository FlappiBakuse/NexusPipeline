using NexusPipeline.Models;

namespace NexusPipeline.Services;

/// <summary>集中解析原始绑定与用户全局覆盖，返回可安全冻结到执行计划中的副本。</summary>
internal static class UserBindingOverrideResolver
{
    public static UserScriptBinding Resolve(NexusUser user, UserScriptBinding raw)
    {
        UserScriptBinding effective = raw.Clone();
        UserBindingOverrides overrides = Normalize(user.BindingOverrides);
        if (overrides.General?.SyncEnabled == true)
        {
            effective.Enabled = overrides.General.Enabled;
            effective.RunDays = overrides.General.RunDays;
            effective.MaxSuccessfulRunsPerDay = overrides.General.MaxSuccessfulRunsPerDay;
        }
        if (overrides.Notification?.SyncEnabled == true)
        {
            effective.NotifyEnabled = overrides.Notification.NotifyEnabled;
            effective.SmtpTo = overrides.Notification.SmtpTo;
        }
        if (overrides.Advanced?.SyncEnabled == true)
        {
            effective.PreRunScript = overrides.Advanced.PreRunScript;
            effective.PreRunOnceOnly = overrides.Advanced.PreRunOnceOnly;
            effective.PostRunScript = overrides.Advanced.PostRunScript;
            effective.PostRunOnFinalOnly = overrides.Advanced.PostRunOnFinalOnly;
        }
        return effective;
    }

    public static UserBindingOverrides Normalize(UserBindingOverrides? candidate)
    {
        UserBindingOverrides source = candidate?.Clone() ?? new UserBindingOverrides();
        source.General ??= new UserGeneralOverride();
        source.Notification ??= new UserNotificationOverride();
        source.Advanced ??= new UserAdvancedOverride();
        source.Notification.SmtpTo = source.Notification.SmtpTo?.Trim() ?? "";
        source.Advanced.PreRunScript = source.Advanced.PreRunScript?.Trim() ?? "";
        source.Advanced.PostRunScript = source.Advanced.PostRunScript?.Trim() ?? "";
        return source;
    }

    public static (bool General, bool Notification, bool Advanced) Locks(NexusUser user)
    {
        UserBindingOverrides overrides = Normalize(user.BindingOverrides);
        return (
            overrides.General?.SyncEnabled == true,
            overrides.Notification?.SyncEnabled == true,
            overrides.Advanced?.SyncEnabled == true);
    }
}
