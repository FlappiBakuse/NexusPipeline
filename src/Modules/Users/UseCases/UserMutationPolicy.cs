using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Shared.Results;

namespace NexusPipeline.Modules.Users.UseCases;

/// <summary>用户绑定的准入、覆盖锁与专项插件可用性策略。</summary>
internal sealed partial class UserCommands
{
    private static UserMutationBlock ResourceBusy(string message) =>
        new("resource_busy", message);

    private static UserMutationBlock GlobalOverride(string message) =>
        new("global_override_active", message);

    private static OperationResult<T> MutationConflict<T>(UserMutationBlock block) =>
        Conflict<T>(block.Code, block.Message);

    private static UserScriptBinding NormalizeBindingForUser(
        NexusUser user,
        UserScriptBinding candidate)
    {
        UserBindingOverrides overrides = user.BindingOverrides ?? new UserBindingOverrides();
        UserScriptBinding normalized = candidate.Clone();
        if (overrides.General?.SyncEnabled == true)
        {
            normalized.Enabled = true;
            normalized.RunDays = -1;
            normalized.MaxSuccessfulRunsPerDay = -1;
        }
        if (overrides.Notification?.SyncEnabled == true)
        {
            normalized.NotifyEnabled = true;
            normalized.SmtpTo = "";
        }
        if (overrides.Advanced?.SyncEnabled == true)
        {
            normalized.PreRunScript = "";
            normalized.PreRunOnceOnly = false;
            normalized.PostRunScript = "";
            normalized.PostRunOnFinalOnly = false;
        }
        return normalized;
    }

    private static UserMutationBlock? CheckLockedBindingUpdate(
        NexusUser user,
        UserScriptBinding previous,
        UserScriptBinding candidate)
    {
        UserBindingOverrides overrides = user.BindingOverrides ?? new UserBindingOverrides();
        if (overrides.General?.SyncEnabled == true
            && (previous.Enabled != candidate.Enabled
                || previous.RunDays != candidate.RunDays
                || previous.MaxSuccessfulRunsPerDay != candidate.MaxSuccessfulRunsPerDay))
        {
            return GlobalOverride("全局管理正在同步通用设置，请先关闭全局同步或保持绑定原始值不变");
        }
        if (overrides.Notification?.SyncEnabled == true
            && (previous.NotifyEnabled != candidate.NotifyEnabled
                || !string.Equals(previous.SmtpTo, candidate.SmtpTo, StringComparison.Ordinal)))
        {
            return GlobalOverride("全局管理正在同步通知设置，请先关闭全局同步或保持绑定原始值不变");
        }
        if (overrides.Advanced?.SyncEnabled == true
            && (!string.Equals(previous.PreRunScript, candidate.PreRunScript, StringComparison.Ordinal)
                || previous.PreRunOnceOnly != candidate.PreRunOnceOnly
                || !string.Equals(previous.PostRunScript, candidate.PostRunScript, StringComparison.Ordinal)
                || previous.PostRunOnFinalOnly != candidate.PostRunOnFinalOnly))
        {
            return GlobalOverride("全局管理正在同步高级设置，请先关闭全局同步或保持绑定原始值不变");
        }
        return null;
    }

}
