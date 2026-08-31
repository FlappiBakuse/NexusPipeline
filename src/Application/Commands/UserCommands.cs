using NexusPipeline.App.Abstractions;
using NexusPipeline.App.Contracts;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.App.Commands;

/// <summary>
/// 全局用户、绑定与头像的应用命令。配置数据与运行租约的协调统一在服务进程内完成。
/// </summary>
internal static class UserCommands
{
    private const int MaxAvatarBytes = 5 * 1024 * 1024;

    public static OperationResult<NexusUser> Create(
        string? name,
        string? remark,
        string source = Audit.Web)
    {
        if (ValidateName(name) is string nameError)
        {
            return Validation<NexusUser>(nameError);
        }
        if (ValidateRemark(remark) is string remarkError)
        {
            return Validation<NexusUser>(remarkError);
        }
        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? created = null;
        string? error = null;
        try
        {
            ctx.Center.WithAdmissionCoordination(() =>
            {
                lock (ctx.DataLock)
                {
                    string normalizedName = name!.Trim();
                    error = Limits.CheckGlobalUserCount(ctx.Users.Count)
                        ?? (ctx.Users.Any(user => string.Equals(user.Name, normalizedName, StringComparison.OrdinalIgnoreCase))
                            ? "用户名重复：全局用户已存在同名用户"
                            : null);
                    if (error is not null)
                    {
                        return;
                    }
                    created = new NexusUser
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Index = ctx.Users.Count == 0 ? 0 : ctx.Users.Max(user => user.Index) + 1,
                        Name = normalizedName,
                        Remark = remark?.Trim() ?? "",
                    };
                    ctx.Users.Add(created);
                    try
                    {
                        DataStore.SaveUsers(ctx.Users);
                    }
                    catch
                    {
                        ctx.Users.Remove(created);
                        throw;
                    }
                }
            });
            if (error is not null)
            {
                return Validation<NexusUser>(error);
            }
            Audit.Log(source, "添加全局用户", $"{created!.Name}（id={created.Id}）");
            return OperationResult<NexusUser>.Ok(created!);
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
        if (ValidateName(name) is string nameError)
        {
            return Validation<NexusUser>(nameError);
        }
        if (ValidateRemark(remark) is string remarkError)
        {
            return Validation<NexusUser>(remarkError);
        }

        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? target = ctx.FindUser(userId);
        if (target is null)
        {
            return NotFound<NexusUser>($"未找到用户：{userId}");
        }
        string? error = null;
        try
        {
            ctx.Center.WithAdmissionCoordination(() =>
            {
                error = CheckUserMutationBusy(ctx, target);
                if (error is not null)
                {
                    return;
                }
                lock (ctx.DataLock)
                {
                    string normalizedName = name!.Trim();
                    if (ctx.Users.Any(user => !ReferenceEquals(user, target)
                        && string.Equals(user.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
                    {
                        error = "用户名重复：全局用户已存在同名用户";
                        return;
                    }
                    string oldName = target.Name;
                    string oldRemark = target.Remark;
                    target.Name = normalizedName;
                    target.Remark = remark?.Trim() ?? "";
                    try
                    {
                        DataStore.SaveUsers(ctx.Users);
                    }
                    catch
                    {
                        target.Name = oldName;
                        target.Remark = oldRemark;
                        throw;
                    }
                }
            });
            if (error is not null)
            {
                return IsBusy(error)
                    ? Conflict<NexusUser>("resource_busy", error)
                    : Validation<NexusUser>(error);
            }
            Audit.Log(source, "编辑全局用户", $"{userId} → {target.Name}");
            return OperationResult<NexusUser>.Ok(target);
        }
        catch (Exception ex)
        {
            return Internal<NexusUser>(ex);
        }
    }

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
        NexusUser? target = ctx.FindUser(userId);
        if (target is null)
        {
            return NotFound<UserBindingOverrides>($"未找到用户：{userId}");
        }

        string? error = null;
        try
        {
            ctx.Center.WithAdmissionCoordination(() =>
            {
                error = CheckUserMutationBusy(ctx, target);
                if (error is not null)
                {
                    return;
                }
                lock (ctx.DataLock)
                {
                    UserBindingOverrides previous = target.BindingOverrides?.Clone() ?? new UserBindingOverrides();
                    target.BindingOverrides = normalized.Clone();
                    try
                    {
                        DataStore.SaveUsers(ctx.Users);
                    }
                    catch
                    {
                        target.BindingOverrides = previous;
                        throw;
                    }
                }
            });
            if (error is not null)
            {
                return Conflict<UserBindingOverrides>("resource_busy", error);
            }
            ctx.Scheduler.RevalidatePendingPlans();
            Audit.Log(source, "调整用户全局绑定设置", $"{target.Name}（id={target.Id}）");
            return OperationResult<UserBindingOverrides>.Ok(target.BindingOverrides.Clone());
        }
        catch (Exception ex)
        {
            return Internal<UserBindingOverrides>(ex);
        }
    }

    /// <summary>删除不存在的 ID 仍返回成功，保持既有 Web API 的幂等语义。</summary>
    public static OperationResult<bool> Delete(
        string userId,
        string? confirmName,
        string source = Audit.Web)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? target = ctx.FindUser(userId);
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
        try
        {
            ctx.Center.WithAdmissionCoordination(() =>
            {
                error = CheckUserMutationBusy(ctx, target);
                if (error is not null)
                {
                    return;
                }
                lock (ctx.DataLock)
                {
                    int index = ctx.Users.IndexOf(target);
                    if (index < 0)
                    {
                        error = "用户不存在";
                        return;
                    }
                    ctx.Users.RemoveAt(index);
                    try
                    {
                        DataStore.SaveUsers(ctx.Users);
                    }
                    catch
                    {
                        ctx.Users.Insert(index, target);
                        throw;
                    }
                }
                foreach (UserScriptBinding binding in bindings)
                {
                    UserConfigManager.RemoveUserData(binding.ScriptInstanceId, target.Id);
                }
                DeleteAvatarFiles(target.Id);
                ctx.Plugins.DeleteUserData(target.Id);
            });
            if (error is not null)
            {
                return IsBusy(error)
                    ? Conflict<bool>("resource_busy", error)
                    : Validation<bool>(error);
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
                lock (ctx.DataLock)
                {
                    if (ids is null || ids.Count != ctx.Users.Count
                        || ids.Any(string.IsNullOrWhiteSpace)
                        || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Count)
                    {
                        error = "用户顺序名单缺失或与当前全局用户列表不一致";
                        return;
                    }
                    HashSet<string> existing = new(ctx.Users.Select(user => user.Id), StringComparer.OrdinalIgnoreCase);
                    if (ids.Any(id => !existing.Contains(id)))
                    {
                        error = "用户顺序名单与当前全局用户列表不一致";
                        return;
                    }
                    Dictionary<string, NexusUser> byId = ctx.Users.ToDictionary(user => user.Id, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < ids.Count; i++)
                    {
                        byId[ids[i]].Index = i;
                    }
                    DataStore.SaveUsers(ctx.Users);
                }
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

    public static OperationResult<bool> ReorderBindings(
        string userId,
        IReadOnlyList<string>? ids,
        string source = Audit.Web)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? user = ctx.FindUser(userId);
        if (user is null)
        {
            return NotFound<bool>("未找到用户");
        }

        string? error = null;
        try
        {
            ctx.Center.WithAdmissionCoordination(() =>
            {
                error = CheckUserMutationBusy(ctx, user);
                if (error is not null)
                {
                    return;
                }
                lock (ctx.DataLock)
                {
                    List<UserScriptBinding> current = user.Bindings.ToList();
                    if (ids is null || ids.Count != current.Count
                        || ids.Any(string.IsNullOrWhiteSpace)
                        || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
                    {
                        error = "绑定顺序名单缺失或与当前用户绑定列表不一致";
                        return;
                    }

                    HashSet<string> existing = new(current.Select(binding => binding.ScriptInstanceId), StringComparer.Ordinal);
                    if (existing.Count != current.Count || ids.Any(id => !existing.Contains(id)))
                    {
                        error = "绑定顺序名单与当前用户绑定列表不一致";
                        return;
                    }

                    Dictionary<string, UserScriptBinding> byId = current.ToDictionary(
                        binding => binding.ScriptInstanceId,
                        StringComparer.Ordinal);
                    List<UserScriptBinding> ordered = ids.Select(id => byId[id]).ToList();
                    user.Bindings.Clear();
                    user.Bindings.AddRange(ordered);
                    try
                    {
                        DataStore.SaveUsers(ctx.Users);
                    }
                    catch
                    {
                        user.Bindings.Clear();
                        user.Bindings.AddRange(current);
                        throw;
                    }
                }
            });
            if (error is not null)
            {
                return IsBusy(error)
                    ? Conflict<bool>("resource_busy", error)
                    : Validation<bool>(error);
            }
            ctx.Scheduler.RevalidatePendingPlans();
            Audit.Log(source, "调整用户绑定顺序", $"{user.Name} / {ids!.Count} 个脚本实例");
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
    }

    public static OperationResult<UserScriptBinding> AddBinding(
        string userId,
        UserScriptBinding candidate,
        string source = Audit.Web)
    {
        if (string.IsNullOrWhiteSpace(candidate.ScriptInstanceId))
        {
            return Validation<UserScriptBinding>("必须指定脚本实例");
        }
        if (ValidateSmtp(candidate.SmtpTo) is string smtpError)
        {
            return Validation<UserScriptBinding>(smtpError);
        }
        if (ValidateRunDays(candidate.RunDays) is string runDaysError)
        {
            return Validation<UserScriptBinding>(runDaysError);
        }
        if (ValidateMaxSuccessfulRunsPerDay(candidate.MaxSuccessfulRunsPerDay) is string maxSuccessfulRunsError)
        {
            return Validation<UserScriptBinding>(maxSuccessfulRunsError);
        }
        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? user = ctx.FindUser(userId);
        ScriptInstance? script = ctx.FindScript(candidate.ScriptInstanceId);
        if (user is null || script is null)
        {
            return NotFound<UserScriptBinding>("用户或脚本实例不存在");
        }
        if (CheckScriptPluginAvailability(ctx, script) is string pluginError)
        {
            return Validation<UserScriptBinding>(pluginError);
        }
        candidate = NormalizeBinding(candidate, script.Id);
        string? error = null;
        SemaphoreSlim gate = ScriptConfigGate.Get(script.Id);
        bool gateHeld = false;
        try
        {
            ctx.Center.WithAdmissionCoordination(() =>
            {
                try
                {
                    if (ctx.Center.FindLeases(script.Id).Count > 0)
                    {
                        error = "脚本正在运行，无法新增绑定";
                        return;
                    }
                    if (UserConfigManager.EditSessions.Values.Any(session => session.Script.Id == script.Id))
                    {
                        error = "脚本正在编辑配置中，无法新增绑定";
                        return;
                    }
                    if (!gate.Wait(0))
                    {
                        error = "脚本正在运行或编辑配置中，无法新增绑定";
                        return;
                    }
                    gateHeld = true;
                    error = CheckBindingBusy(ctx, user.Id, script.Id);
                    if (error is not null)
                    {
                        return;
                    }

                    ScriptInstance snapshotScript;
                    lock (ctx.DataLock)
                    {
                        if (user.Bindings.Any(item => string.Equals(item.ScriptInstanceId, script.Id, StringComparison.Ordinal)))
                        {
                            error = "该用户已绑定此脚本实例";
                            return;
                        }
                        int current = ctx.Users.Sum(item => item.Bindings.Count(binding =>
                            string.Equals(binding.ScriptInstanceId, script.Id, StringComparison.Ordinal)));
                        error = Limits.CheckUserCount(current);
                        if (error is not null)
                        {
                            return;
                        }
                        snapshotScript = script.Clone();
                    }

                    string? snapshotError = UserConfigManager.SnapshotOnAddUser(
                        snapshotScript,
                        user.Id,
                        ctx.Resolve<IPluginCapabilityResolver>());
                    if (snapshotError is not null)
                    {
                        error = "初始配置快照失败：" + snapshotError;
                        return;
                    }

                    lock (ctx.DataLock)
                    {
                        if (user.Bindings.Any(item => string.Equals(item.ScriptInstanceId, script.Id, StringComparison.Ordinal)))
                        {
                            error = "该用户已绑定此脚本实例";
                            return;
                        }
                        candidate = NormalizeBindingForUser(user, candidate);
                        user.Bindings.Add(candidate);
                        try
                        {
                            DataStore.SaveUsers(ctx.Users);
                        }
                        catch
                        {
                            user.Bindings.Remove(candidate);
                            throw;
                        }
                    }
                }
                finally
                {
                    if (gateHeld)
                    {
                        gate.Release();
                        gateHeld = false;
                    }
                }
            });
            if (error is not null)
            {
                return IsBusy(error)
                    ? Conflict<UserScriptBinding>("resource_busy", error)
                    : Validation<UserScriptBinding>(error);
            }
            ctx.Scheduler.RevalidatePendingPlans();
            Audit.Log(source, "绑定全局用户脚本", $"{user.Name} / {script.Name}");
            return OperationResult<UserScriptBinding>.Ok(candidate);
        }
        catch (Exception ex)
        {
            return Internal<UserScriptBinding>(ex);
        }
        finally
        {
            if (gateHeld)
            {
                gate.Release();
            }
        }
    }

    public static OperationResult<UserScriptBinding> UpdateBinding(
        string userId,
        string scriptId,
        UserScriptBinding candidate,
        string source = Audit.Web)
    {
        if (ValidateSmtp(candidate.SmtpTo) is string smtpError)
        {
            return Validation<UserScriptBinding>(smtpError);
        }
        if (ValidateRunDays(candidate.RunDays) is string runDaysError)
        {
            return Validation<UserScriptBinding>(runDaysError);
        }
        if (ValidateMaxSuccessfulRunsPerDay(candidate.MaxSuccessfulRunsPerDay) is string maxSuccessfulRunsError)
        {
            return Validation<UserScriptBinding>(maxSuccessfulRunsError);
        }
        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? user = ctx.FindUser(userId);
        UserScriptBinding? existing = user?.Bindings.FirstOrDefault(binding => binding.ScriptInstanceId == scriptId);
        if (user is null || existing is null)
        {
            return NotFound<UserScriptBinding>("用户绑定不存在");
        }
        ScriptInstance? script = ctx.FindScript(scriptId);
        if (script is not null && CheckScriptPluginAvailability(ctx, script) is string pluginError)
        {
            return Validation<UserScriptBinding>(pluginError);
        }
        string? error = null;
        bool globalOverrideConflict = false;
        try
        {
            ctx.Center.WithAdmissionCoordination(() =>
            {
                error = CheckBindingBusy(ctx, user.Id, scriptId);
                if (error is not null)
                {
                    return;
                }
                lock (ctx.DataLock)
                {
                    UserScriptBinding old = existing.Clone();
                    UserScriptBinding replacement = NormalizeBinding(candidate, scriptId);
                    if (CheckLockedBindingUpdate(user, old, replacement) is string overrideError)
                    {
                        error = overrideError;
                        globalOverrideConflict = true;
                        return;
                    }
                    int index = user.Bindings.IndexOf(existing);
                    user.Bindings[index] = replacement;
                    try
                    {
                        DataStore.SaveUsers(ctx.Users);
                    }
                    catch
                    {
                        user.Bindings[index] = old;
                        throw;
                    }
                    existing = replacement;
                }
            });
            if (error is not null)
            {
                return globalOverrideConflict
                    ? Conflict<UserScriptBinding>("global_override_active", GlobalOverrideMessage(error))
                    : Conflict<UserScriptBinding>("resource_busy", error);
            }
            ctx.Scheduler.RevalidatePendingPlans();
            return OperationResult<UserScriptBinding>.Ok(existing!);
        }
        catch (Exception ex)
        {
            return Internal<UserScriptBinding>(ex);
        }
    }

    public static OperationResult<bool> DeleteBinding(
        string userId,
        string scriptId,
        string source = Audit.Web)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? user = ctx.FindUser(userId);
        UserScriptBinding? binding = user?.Bindings.FirstOrDefault(item => item.ScriptInstanceId == scriptId);
        if (user is null || binding is null)
        {
            return NotFound<bool>("用户绑定不存在");
        }
        string? error = null;
        try
        {
            ctx.Center.WithAdmissionCoordination(() =>
            {
                error = CheckBindingBusy(ctx, user.Id, scriptId);
                if (error is not null)
                {
                    return;
                }
                lock (ctx.DataLock)
                {
                    int index = user.Bindings.IndexOf(binding);
                    if (index < 0)
                    {
                        error = "绑定不存在";
                        return;
                    }
                    user.Bindings.RemoveAt(index);
                    try
                    {
                        DataStore.SaveUsers(ctx.Users);
                        UserConfigManager.RemoveUserData(scriptId, user.Id);
                        ctx.Plugins.DeleteUserScriptData(user.Id, scriptId);
                    }
                    catch
                    {
                        user.Bindings.Insert(index, binding);
                        try { DataStore.SaveUsers(ctx.Users); } catch { }
                        throw;
                    }
                }
            });
            if (error is not null)
            {
                return Conflict<bool>("resource_busy", error);
            }
            ctx.Scheduler.RevalidatePendingPlans();
            Audit.Log(source, "解除全局用户脚本绑定", $"{user.Name} / {scriptId}");
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
    }

    public static OperationResult<bool> SetAvatar(string userId, string? mimeType, byte[]? data)
    {
        if (RuntimeContext.Instance.FindUser(userId) is null)
        {
            return NotFound<bool>($"未找到用户：{userId}");
        }
        string mime = mimeType?.Trim().ToLowerInvariant() ?? "";
        string extension = mime switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/webp" => "webp",
            _ => "",
        };
        if (extension.Length == 0 || data is null || data.Length == 0
            || data.Length > MaxAvatarBytes || !HasMatchingMagic(mime, data))
        {
            return Validation<bool>("头像文件格式或大小不符合要求（上限 5 MiB）");
        }
        try
        {
            string dir = Path.Combine(AppPaths.UserAssetsDir, userId);
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "avatar." + extension);
            File.WriteAllBytes(target, data);
            foreach (string file in Directory.GetFiles(dir, "avatar.*"))
            {
                if (!string.Equals(file, target, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
    }

    public static OperationResult<bool> RemoveAvatar(string userId)
    {
        if (RuntimeContext.Instance.FindUser(userId) is null)
        {
            return NotFound<bool>($"未找到用户：{userId}");
        }
        try
        {
            DeleteAvatarFiles(userId);
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
    }

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

    private static string? CheckLockedBindingUpdate(
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
            return "global_override_active|全局管理正在同步通用设置，请先关闭全局同步或保持绑定原始值不变";
        }
        if (overrides.Notification?.SyncEnabled == true
            && (previous.NotifyEnabled != candidate.NotifyEnabled
                || !string.Equals(previous.SmtpTo, candidate.SmtpTo, StringComparison.Ordinal)))
        {
            return "global_override_active|全局管理正在同步通知设置，请先关闭全局同步或保持绑定原始值不变";
        }
        if (overrides.Advanced?.SyncEnabled == true
            && (!string.Equals(previous.PreRunScript, candidate.PreRunScript, StringComparison.Ordinal)
                || previous.PreRunOnceOnly != candidate.PreRunOnceOnly
                || !string.Equals(previous.PostRunScript, candidate.PostRunScript, StringComparison.Ordinal)
                || previous.PostRunOnFinalOnly != candidate.PostRunOnFinalOnly))
        {
            return "global_override_active|全局管理正在同步高级设置，请先关闭全局同步或保持绑定原始值不变";
        }
        return null;
    }

    private static UserScriptBinding NormalizeBinding(UserScriptBinding candidate, string scriptId)
    {
        return new UserScriptBinding
        {
            ScriptInstanceId = scriptId.Trim(),
            Enabled = candidate.Enabled,
            PreRunScript = candidate.PreRunScript.Trim(),
            PreRunOnceOnly = candidate.PreRunOnceOnly,
            PostRunScript = candidate.PostRunScript.Trim(),
            PostRunOnFinalOnly = candidate.PostRunOnFinalOnly,
            NotifyEnabled = candidate.NotifyEnabled,
            SmtpTo = candidate.SmtpTo.Trim(),
            RunDays = candidate.RunDays,
            MaxSuccessfulRunsPerDay = candidate.MaxSuccessfulRunsPerDay,
        };
    }

    private static string? CheckUserMutationBusy(RuntimeContext ctx, NexusUser user)
    {
        if (ctx.Scheduler.HasPendingUser(user.Id))
        {
            return "用户已存在待执行的冻结计划，暂时无法修改";
        }
        foreach (UserScriptBinding binding in user.Bindings)
        {
            if (CheckBindingBusy(ctx, user.Id, binding.ScriptInstanceId) is string error)
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

    private static string? CheckBindingBusy(RuntimeContext ctx, string userId, string scriptId)
    {
        if (ctx.Center.FindLeases(scriptId, userId).Count > 0)
        {
            return "用户绑定正在运行，无法修改";
        }
        if (UserConfigManager.EditSessions.Values.Any(session =>
            session.Script.Id == scriptId
            && string.Equals(session.Mark.UserName, userId, StringComparison.OrdinalIgnoreCase)))
        {
            return "用户绑定正在编辑配置，无法修改";
        }
        if (ctx.Scheduler.HasPendingBinding(userId, scriptId))
        {
            return "该用户绑定已存在待执行的冻结计划，暂时无法修改";
        }
        return null;
    }

    private static string? ValidateName(string? name)
    {
        return string.IsNullOrWhiteSpace(name) || !UserNameRule.IsValidName(name.Trim())
            ? "用户名不能为空且不能包含非法字符"
            : Limits.CheckNameBytes(name.Trim(), AppFixedLimits.MaxEntityNameBytes, "用户名");
    }

    private static string? ValidateRemark(string? remark) =>
        Limits.CheckNameBytes(remark?.Trim() ?? "", AppFixedLimits.MaxUserRemarkBytes, "备注");

    private static string? ValidateRunDays(int value) => Limits.CheckRunDays(value);

    private static string? ValidateMaxSuccessfulRunsPerDay(int value) =>
        Limits.CheckMaxSuccessfulRunsPerDay(value);

    private static string? ValidateSmtp(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : SmtpSender.ValidateRecipients(value.Trim());

    private static bool IsBusy(string error) =>
        error.Contains("运行", StringComparison.Ordinal)
        || error.Contains("编辑", StringComparison.Ordinal)
        || error.Contains("待执行", StringComparison.Ordinal);

    private static string GlobalOverrideMessage(string error)
    {
        int separator = error.IndexOf('|');
        return separator >= 0 ? error[(separator + 1)..] : error;
    }

    private static void DeleteAvatarFiles(string userId)
    {
        string dir = Path.Combine(AppPaths.UserAssetsDir, userId);
        if (!Directory.Exists(dir))
        {
            return;
        }
        foreach (string file in Directory.GetFiles(dir, "avatar.*"))
        {
            try { File.Delete(file); } catch (Exception ex) { Logger.Warn($"[警告] 清理用户头像失败（{file}）：{ex.Message}"); }
        }
        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { }
    }

    private static bool HasMatchingMagic(string mime, byte[] data)
    {
        return mime switch
        {
            "image/png" => data.Length >= 8
                && data.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "image/jpeg" => data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF,
            "image/webp" => data.Length >= 12
                && data.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                && data.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };
    }

    private static OperationResult<T> Validation<T>(string message) =>
        OperationResult<T>.Failure("validation_error", message, OperationErrorKind.Validation);

    private static OperationResult<T> NotFound<T>(string message) =>
        OperationResult<T>.Failure("not_found", message, OperationErrorKind.NotFound);

    private static OperationResult<T> Conflict<T>(string code, string message) =>
        OperationResult<T>.Failure(code, message, OperationErrorKind.Conflict);

    private static OperationResult<T> LeaseConflict<T>(
        IReadOnlyList<ExecutionLeaseReference> leases,
        string resource,
        string? failureCode = null)
    {
        return failureCode == "host_maintenance"
            ? OperationResult<T>.Failure(
                "host_maintenance",
                "宿主正在进行维护操作，暂不能修改运行配置",
                OperationErrorKind.Conflict)
            : OperationResult<T>.Failure(
                "execution_resource_in_use",
                $"执行计划正在引用资源「{resource}」，当前无法修改；请等待相关运行结束",
                OperationErrorKind.Conflict,
                leases.Select(lease => lease.RunId).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static OperationResult<T> Internal<T>(Exception exception) =>
        OperationResult<T>.Failure("internal_error", exception.Message, OperationErrorKind.Internal);
}
