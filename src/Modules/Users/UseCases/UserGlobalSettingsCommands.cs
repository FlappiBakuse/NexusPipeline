using NexusPipeline.Modules.Scheduling;
using NexusPipeline.Modules.Users.Bindings;
using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Results;
using NexusPipeline.Modules.Users.Persistence;

namespace NexusPipeline.Modules.Users.UseCases;

internal sealed partial class UserCommands
{
    public OperationResult<UserBindingOverrides> UpdateGlobalSettings(
        string userId,
        UserBindingOverrides? candidate,
        string source = Audit.Web)
    {
        UserBindingOverrides normalized = UserBindingOverrideResolver.Normalize(candidate);
        if (normalized.General.SyncEnabled
            && ValidateRunDays(normalized.General.RunDays) is string runDaysError)
        {
            return Validation<UserBindingOverrides>(
                "global_run_days_invalid",
                runDaysError,
                new Dictionary<string, object?> { ["value"] = normalized.General.RunDays });
        }
        if (normalized.General.SyncEnabled
            && ValidateMaxSuccessfulRunsPerDay(normalized.General.MaxSuccessfulRunsPerDay) is string maxSuccessfulRunsError)
        {
            return Validation<UserBindingOverrides>(
                "global_max_success_invalid",
                maxSuccessfulRunsError,
                new Dictionary<string, object?> { ["value"] = normalized.General.MaxSuccessfulRunsPerDay });
        }
        if (normalized.Notification.SyncEnabled
            && ValidateSmtp(normalized.Notification.SmtpTo) is string smtpError)
        {
            return Validation<UserBindingOverrides>("global_smtp_invalid", smtpError);
        }

        NexusUser? target = _state.Find(userId);
        if (target is null)
        {
            return NotFound<UserBindingOverrides>($"未找到用户：{userId}");
        }

        string? error = null;
        UserMutationBlock? block = null;
        try
        {
            _admission.WithCoordination(() =>
            {
                block = _policy.CheckUser(target);
                if (block is not null)
                {
                    return;
                }
                _state.Mutate(state =>
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
                        UserDefinitionStore.SaveUsers(state.Users);
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
            _plansChanged.RevalidatePendingPlans();
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
