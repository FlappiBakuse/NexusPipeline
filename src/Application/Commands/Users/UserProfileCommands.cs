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
    public static OperationResult<NexusUser> Create(
        string? name,
        string? remark,
        string source = Audit.Web)
    {
        if (ValidateName(name) is ValidationIssue nameError)
        {
            return Validation<NexusUser>(nameError.Code, nameError.Message, nameError.Args);
        }
        if (ValidateRemark(remark) is ValidationIssue remarkError)
        {
            return Validation<NexusUser>(remarkError.Code, remarkError.Message, remarkError.Args);
        }
        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? created = null;
        string? error = null;
        bool duplicateName = false;
        try
        {
            ctx.Center.WithAdmissionCoordination(() =>
            {
                ctx.EntityState.Mutate(state =>
                {
                    string normalizedName = name!.Trim();
                    error = Limits.CheckGlobalUserCount(state.Users.Count);
                    if (error is null && EntityNameRules.HasConflict(state.Users, normalizedName, user => user.Name))
                    {
                        duplicateName = true;
                        error = "用户名重复：全局用户已存在同名用户";
                    }
                    if (error is not null)
                    {
                        return;
                    }
                    created = new NexusUser
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Index = state.Users.Count == 0 ? 0 : state.Users.Max(user => user.Index) + 1,
                        Name = normalizedName,
                        Remark = remark?.Trim() ?? "",
                    };
                    state.Users.Add(created);
                    try
                    {
                        DataStore.SaveUsers(state.Users);
                    }
                    catch
                    {
                        state.Users.Remove(created);
                        throw;
                    }
                });
            });
            if (error is not null)
            {
                return duplicateName
                    ? Conflict<NexusUser>("duplicate_name", error)
                    : Validation<NexusUser>(error);
            }
            Audit.Log(source, "添加全局用户", $"{created!.Name}（id={created.Id}）");
            return OperationResult<NexusUser>.Ok(created!.Clone());
        }
        catch (Exception ex)
        {
            return Internal<NexusUser>(ex);
        }
    }

    public static OperationResult<NexusUser> Update(
        string userId,
        string? name,
        string? remark,
        string source = Audit.Web)
    {
        if (ValidateName(name) is ValidationIssue nameError)
        {
            return Validation<NexusUser>(nameError.Code, nameError.Message, nameError.Args);
        }
        if (ValidateRemark(remark) is ValidationIssue remarkError)
        {
            return Validation<NexusUser>(remarkError.Code, remarkError.Message, remarkError.Args);
        }

        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? target = ctx.EntityState.FindUser(userId);
        if (target is null)
        {
            return NotFound<NexusUser>($"未找到用户：{userId}");
        }
        string? error = null;
        UserMutationBlock? block = null;
        bool duplicateName = false;
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
                    string normalizedName = name!.Trim();
                    if (EntityNameRules.HasConflict(
                            state.Users,
                            normalizedName,
                            user => user.Name,
                            user => string.Equals(user.Id, current.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        duplicateName = true;
                        error = "用户名重复：全局用户已存在同名用户";
                        return;
                    }
                    string oldName = current.Name;
                    string oldRemark = current.Remark;
                    current.Name = normalizedName;
                    current.Remark = remark?.Trim() ?? "";
                    try
                    {
                        DataStore.SaveUsers(state.Users);
                        target = current.Clone();
                    }
                    catch
                    {
                        current.Name = oldName;
                        current.Remark = oldRemark;
                        throw;
                    }
                });
            });
            if (error is not null)
            {
                return duplicateName
                    ? Conflict<NexusUser>("duplicate_name", error)
                    : Validation<NexusUser>(error);
            }
            if (block is not null)
            {
                return MutationConflict<NexusUser>(block);
            }
            Audit.Log(source, "编辑全局用户", $"{userId} → {target.Name}");
            return OperationResult<NexusUser>.Ok(target);
        }
        catch (Exception ex)
        {
            return Internal<NexusUser>(ex);
        }
    }

    public static OperationResult<bool> Delete(
        string userId,
        string? confirmName,
        string source = Audit.Web)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? target = ctx.EntityState.FindUser(userId);
        if (target is null)
        {
            return NotFound<bool>($"未找到用户：{userId}");
        }
        if (!string.Equals(confirmName, target.Name, StringComparison.Ordinal))
        {
            return Validation<bool>("请完整输入用户名以确认删除");
        }

        List<UserScriptBinding> bindings = target.Bindings.Select(binding => binding.Clone()).ToList();
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
                    int index = current is null ? -1 : state.Users.IndexOf(current);
                    if (index < 0)
                    {
                        error = "用户不存在";
                        return;
                    }
                    state.Users.RemoveAt(index);
                    try
                    {
                        DataStore.SaveUsers(state.Users);
                    }
                    catch
                    {
                        state.Users.Insert(index, current!);
                        throw;
                    }
                });
                foreach (UserScriptBinding binding in bindings)
                {
                    UserConfigManager.RemoveUserData(binding.ScriptInstanceId, target.Id);
                }
                DeleteAvatarFiles(target.Id);
                ctx.Plugins.DeleteUserData(target.Id);
            });
            if (error is not null)
            {
                return Validation<bool>(error);
            }
            if (block is not null)
            {
                return MutationConflict<bool>(block);
            }
            Audit.Log(source, "删除全局用户", $"{target.Name}（id={target.Id}）");
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
    }

    public static OperationResult<bool> Reorder(IReadOnlyList<string>? ids, string source = Audit.Web)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        try
        {
            string? error = null;
            ctx.Center.WithAdmissionCoordination(() =>
            {
                ctx.EntityState.Mutate(state =>
                {
                    if (ids is null || ids.Count != state.Users.Count
                        || ids.Any(string.IsNullOrWhiteSpace)
                        || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Count)
                    {
                        error = "用户顺序名单缺失或与当前全局用户列表不一致";
                        return;
                    }
                    HashSet<string> existing = new(state.Users.Select(user => user.Id), StringComparer.OrdinalIgnoreCase);
                    if (ids.Any(id => !existing.Contains(id)))
                    {
                        error = "用户顺序名单与当前全局用户列表不一致";
                        return;
                    }
                    Dictionary<string, NexusUser> byId = state.Users.ToDictionary(user => user.Id, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < ids.Count; i++)
                    {
                        byId[ids[i]].Index = i;
                    }
                    DataStore.SaveUsers(state.Users);
                });
            });
            if (error is not null)
            {
                return Validation<bool>(error);
            }
            ctx.Scheduler.RevalidatePendingPlans();
            Audit.Log(source, "调整全局用户顺序", $"{ids!.Count} 个用户");
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
    }

}
