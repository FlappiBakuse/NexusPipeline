using NexusPipeline.Modules.Scheduling;
using NexusPipeline.Modules.Settings.Validation;
using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Naming;
using NexusPipeline.Shared.Results;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Users.Persistence;

namespace NexusPipeline.Modules.Users.UseCases;

internal sealed partial class UserCommands
{
    public OperationResult<NexusUser> Create(
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
        NexusUser? created = null;
        string? error = null;
        bool duplicateName = false;
        try
        {
            _admission.WithCoordination(() =>
            {
                _state.Mutate(state =>
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
                        UserDefinitionStore.SaveUsers(state.Users);
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

    public OperationResult<NexusUser> Update(
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

        NexusUser? target = _state.Find(userId);
        if (target is null)
        {
            return NotFound<NexusUser>($"未找到用户：{userId}");
        }
        string? error = null;
        UserMutationBlock? block = null;
        bool duplicateName = false;
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
                        UserDefinitionStore.SaveUsers(state.Users);
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

    public OperationResult<bool> Delete(
        string userId,
        string? confirmName,
        string source = Audit.Web)
    {
        NexusUser? target = _state.Find(userId);
        if (target is null)
        {
            return NotFound<bool>($"未找到用户：{userId}");
        }
        if (!string.Equals(confirmName, target.Name, StringComparison.Ordinal))
        {
            return Validation<bool>("请完整输入用户名以确认删除");
        }

        try
        {
            UserDeletionResult result = _deletion.Execute(userId);
            if (!result.Allowed)
            {
                return result.FailureCode == "resource_busy"
                    ? Conflict<bool>(result.FailureCode, result.FailureMessage ?? "用户资源正在使用")
                    : Validation<bool>(result.FailureMessage ?? "用户删除被拒绝");
            }
            Audit.Log(source, "删除全局用户", $"{target.Name}（id={target.Id}）");
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
    }

    public OperationResult<bool> Reorder(IReadOnlyList<string>? ids, string source = Audit.Web)
    {
        try
        {
            string? error = null;
            _admission.WithCoordination(() =>
            {
                _state.Mutate(state =>
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
                    UserDefinitionStore.SaveUsers(state.Users);
                });
            });
            if (error is not null)
            {
                return Validation<bool>(error);
            }
            _plansChanged.RevalidatePendingPlans();
            Audit.Log(source, "调整全局用户顺序", $"{ids!.Count} 个用户");
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
    }

}
