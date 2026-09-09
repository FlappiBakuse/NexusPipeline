using NexusPipeline.App.Abstractions;
using NexusPipeline.App.Contracts;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;

namespace NexusPipeline.App.Commands;

/// <summary>用户绑定的准入、覆盖锁与专项插件可用性策略。</summary>
internal static partial class UserCommands
{
    private sealed record UserMutationBlock(string Code, string Message);

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

    private static UserMutationBlock? CheckUserMutationBusy(RuntimeContext ctx, NexusUser user)
    {
        if (ctx.Scheduler.HasPendingUser(user.Id))
        {
            return ResourceBusy("用户已存在待执行的冻结计划，暂时无法修改");
        }
        foreach (UserScriptBinding binding in user.Bindings)
        {
            if (CheckBindingBusy(ctx, user.Id, binding.ScriptInstanceId) is UserMutationBlock error)
            {
                return error;
            }
        }
        return null;
    }

    private static string? CheckScriptPluginAvailability(RuntimeContext ctx, ScriptInstance script) =>
        PluginAvailability.GetUnavailableReason(
            script,
            ctx.Resolve<IPluginAvailability>());

    private static UserMutationBlock? CheckBindingBusy(RuntimeContext ctx, string userId, string scriptId)
    {
        if (ctx.Center.FindLeases(scriptId, userId).Count > 0)
        {
            return ResourceBusy("用户绑定正在运行，无法修改");
        }
        if (UserConfigManager.EditSessions.Values.Any(session =>
            session.Script.Id == scriptId
            && string.Equals(session.Mark.UserId, userId, StringComparison.OrdinalIgnoreCase)))
        {
            return ResourceBusy("用户绑定正在编辑配置，无法修改");
        }
        if (ctx.Scheduler.HasPendingBinding(userId, scriptId))
        {
            return ResourceBusy("该用户绑定已存在待执行的冻结计划，暂时无法修改");
        }
        return null;
    }
}
