using NexusPipeline.App.Abstractions;
using NexusPipeline.App.Contracts;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.App.Commands;

internal static partial class UserCommands
{
    public static OperationResult<UserBindingOverrides> UpdateGlobalSettings(
        string userId,
        UserBindingOverrides? candidate,
        string source = Audit.Web)
    {
        UserBindingOverrides normalized = UserBindingOverrideResolver.Normalize(candidate);
        if (ValidateRunDays(normalized.General.RunDays) is string runDaysError)
        {
            return Validation<UserBindingOverrides>(runDaysError);
        }
        if (ValidateMaxSuccessfulRunsPerDay(normalized.General.MaxSuccessfulRunsPerDay) is string maxSuccessfulRunsError)
        {
            return Validation<UserBindingOverrides>(maxSuccessfulRunsError);
        }
        if (ValidateSmtp(normalized.Notification.SmtpTo) is string smtpError)
        {
            return Validation<UserBindingOverrides>(smtpError);
        }

        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? target = ctx.EntityState.FindUser(userId);
        if (target is null)
        {
            return NotFound<UserBindingOverrides>($"未找到用户：{userId}");
        }

        string? error = null;
        UserMutationBlock? block = null;
        try
        {
            ctx.Center.WithAdmissionCoordination(() =>
            {
                block = CheckUserMutationBusy(ctx, target);
                if (block is not null)
                {
                    return;
                }
                ctx.EntityState.Mutate(state =>
                {
                    NexusUser? current = state.Users.FirstOrDefault(item =>
                        string.Equals(item.Id, target.Id, StringComparison.OrdinalIgnoreCase));
                    if (current is null)
                    {
                        error = "用户不存在";
                        return;
                    }
                    UserBindingOverrides previous = current.BindingOverrides?.Clone() ?? new UserBindingOverrides();
                    current.BindingOverrides = normalized.Clone();
                    try
                    {
                        DataStore.SaveUsers(state.Users);
                        target = current.Clone();
                    }
                    catch
                    {
                        current.BindingOverrides = previous;
                        throw;
                    }
                });
            });
            if (error is not null)
            {
                return Validation<UserBindingOverrides>(error);
            }
            if (block is not null)
            {
                return MutationConflict<UserBindingOverrides>(block);
            }
            ctx.Scheduler.RevalidatePendingPlans();
            Audit.Log(source, "调整用户全局绑定设置", $"{target.Name}（id={target.Id}）");
            return OperationResult<UserBindingOverrides>.Ok(
                (target.BindingOverrides ?? new UserBindingOverrides()).Clone());
        }
        catch (Exception ex)
        {
            return Internal<UserBindingOverrides>(ex);
        }
    }

    /// <summary>删除不存在的 ID 仍返回成功，保持既有 Web API 的幂等语义。</summary>

}
