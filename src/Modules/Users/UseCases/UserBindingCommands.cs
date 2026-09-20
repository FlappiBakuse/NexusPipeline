using NexusPipeline.Modules.Scheduling;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Settings.Validation;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Results;
using NexusPipeline.Modules.Users.Persistence;

namespace NexusPipeline.Modules.Users.UseCases;

internal sealed partial class UserCommands
{
    public OperationResult<bool> ReorderBindings(
        string userId,
        IReadOnlyList<string>? ids,
        string source = Audit.Web)
    {
        NexusUser? user = _state.Find(userId);
        if (user is null)
        {
            return NotFound<bool>("未找到用户");
        }

        string? error = null;
        UserMutationBlock? block = null;
        try
        {
            _admission.WithCoordination(() =>
            {
                block = _policy.CheckUser(user);
                if (block is not null)
                {
                    return;
                }
                _state.Mutate(state =>
                {
                    NexusUser? currentUser = state.Users.FirstOrDefault(item =>
                        string.Equals(item.Id, user.Id, StringComparison.OrdinalIgnoreCase));
                    if (currentUser is null)
                    {
                        error = "用户不存在";
                        return;
                    }
                    List<UserScriptBinding> current = currentUser.Bindings.Select(binding => binding.Clone()).ToList();
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
                    currentUser.Bindings.Clear();
                    currentUser.Bindings.AddRange(ordered);
                    try
                    {
                        UserDefinitionStore.SaveUsers(state.Users);
                        user = currentUser.Clone();
                    }
                    catch
                    {
                        currentUser.Bindings.Clear();
                        currentUser.Bindings.AddRange(current);
                        throw;
                    }
                });
            });
            if (error is not null)
            {
                return Validation<bool>(error);
            }
            if (block is not null)
            {
                return MutationConflict<bool>(block);
            }
            _plansChanged.RevalidatePendingPlans();
            Audit.Log(source, "调整用户绑定顺序", $"{user.Name} / {ids!.Count} 个脚本实例");
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
    }

    public OperationResult<UserScriptBinding> AddBinding(
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
        NexusUser? user = _state.Find(userId);
        ScriptInstance? script = _scripts.FindById(candidate.ScriptInstanceId);
        if (user is null || script is null)
        {
            return NotFound<UserScriptBinding>("用户或脚本实例不存在");
        }
        if (_policy.CheckScriptPluginAvailability(script) is string pluginError)
        {
            return Validation<UserScriptBinding>(pluginError);
        }
        candidate = NormalizeBinding(candidate, script.Id);
        string? error = null;
        UserMutationBlock? block = null;
        IDisposable? gate = null;
        try
        {
            _admission.WithCoordination(() =>
            {
                block = _policy.CheckScript(script.Id);
                if (block is not null)
                {
                    return;
                }
                try
                {
                    gate = _scriptConfigGate.TryAcquire(script.Id);
                    if (gate is null)
                    {
                        block = ResourceBusy("脚本正在运行或编辑配置中，无法新增绑定");
                        return;
                    }
                    block = _policy.CheckBinding(user.Id, script.Id);
                    if (block is not null)
                    {
                        return;
                    }

                    _state.Mutate(state =>
                    {
                        NexusUser? currentUser = state.Users.FirstOrDefault(item =>
                            string.Equals(item.Id, user.Id, StringComparison.OrdinalIgnoreCase));
                        if (currentUser is null)
                        {
                            error = "用户不存在";
                            return;
                        }
                        if (currentUser.Bindings.Any(item => string.Equals(item.ScriptInstanceId, script.Id, StringComparison.Ordinal)))
                        {
                            error = "该用户已绑定此脚本实例";
                            return;
                        }
                        int current = state.Users.Sum(item => item.Bindings.Count(binding =>
                            string.Equals(binding.ScriptInstanceId, script.Id, StringComparison.Ordinal)));
                        error = Limits.CheckUserCount(current);
                        if (error is not null)
                        {
                            return;
                        }
                    });

                    // v0.12.8：绑定不再建立配置快照、不做任何文件动作；初始快照延迟到首次编辑配置或首次运行时建立。

                    _state.Mutate(state =>
                    {
                        NexusUser? currentUser = state.Users.FirstOrDefault(item =>
                            string.Equals(item.Id, user.Id, StringComparison.OrdinalIgnoreCase));
                        if (currentUser is null)
                        {
                            error = "用户不存在";
                            return;
                        }
                        if (currentUser.Bindings.Any(item => string.Equals(item.ScriptInstanceId, script.Id, StringComparison.Ordinal)))
                        {
                            error = "该用户已绑定此脚本实例";
                            return;
                        }
                        candidate = NormalizeBindingForUser(currentUser, candidate);
                        currentUser.Bindings.Add(candidate);
                        try
                        {
                            UserDefinitionStore.SaveUsers(state.Users);
                        }
                        catch
                        {
                            currentUser.Bindings.Remove(candidate);
                            throw;
                        }
                    });
                }
                finally
                {
                    if (gate is not null)
                    {
                        gate.Dispose();
                        gate = null;
                    }
                }
            });
            if (error is not null)
            {
                return Validation<UserScriptBinding>(error);
            }
            if (block is not null)
            {
                return MutationConflict<UserScriptBinding>(block);
            }
            _plansChanged.RevalidatePendingPlans();
            Audit.Log(source, "绑定全局用户脚本", $"{user.Name} / {script.Name}");
            return OperationResult<UserScriptBinding>.Ok(candidate);
        }
        catch (Exception ex)
        {
            return Internal<UserScriptBinding>(ex);
        }
        finally
        {
            if (gate is not null)
            {
                gate.Dispose();
                gate = null;
            }
        }
    }

    public OperationResult<UserScriptBinding> UpdateBinding(
        string userId,
        string scriptId,
        UserBindingUpdateRequest request,
        string source = Audit.Web)
    {
        UserScriptBinding candidate = request.Binding;
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
        NexusUser? user = _state.Find(userId);
        UserScriptBinding? existing = user?.Bindings.FirstOrDefault(binding => binding.ScriptInstanceId == scriptId);
        if (user is null || existing is null)
        {
            return NotFound<UserScriptBinding>("用户绑定不存在");
        }
        ScriptInstance? script = _scripts.FindById(scriptId);
        if (script is not null && _policy.CheckScriptPluginAvailability(script) is string pluginError)
        {
            return Validation<UserScriptBinding>(pluginError);
        }
        string? error = null;
        UserMutationBlock? block = null;
        try
        {
            _admission.WithCoordination(() =>
            {
                block = _policy.CheckBinding(user.Id, scriptId);
                if (block is not null)
                {
                    return;
                }
                _state.Mutate(state =>
                {
                    NexusUser? currentUser = state.Users.FirstOrDefault(item =>
                        string.Equals(item.Id, user.Id, StringComparison.OrdinalIgnoreCase));
                    UserScriptBinding? currentBinding = currentUser?.Bindings.FirstOrDefault(binding =>
                        string.Equals(binding.ScriptInstanceId, scriptId, StringComparison.Ordinal));
                    if (currentUser is null || currentBinding is null)
                    {
                        error = "用户绑定不存在";
                        return;
                    }
                    UserScriptBinding old = currentBinding.Clone();
                    UserScriptBinding replacement = NormalizeBinding(
                        candidate,
                        scriptId,
                        request.ConfigInputsSpecified ? candidate.ConfigInputs : old.ConfigInputs);
                    if (CheckLockedBindingUpdate(currentUser, old, replacement) is UserMutationBlock overrideBlock)
                    {
                        block = overrideBlock;
                        return;
                    }
                    int index = currentUser.Bindings.IndexOf(currentBinding);
                    currentUser.Bindings[index] = replacement;
                    try
                    {
                        UserDefinitionStore.SaveUsers(state.Users);
                    }
                    catch
                    {
                        currentUser.Bindings[index] = old;
                        throw;
                    }
                    existing = replacement;
                });
            });
            if (error is not null)
            {
                return Conflict<UserScriptBinding>("resource_busy", error);
            }
            if (block is not null)
            {
                return MutationConflict<UserScriptBinding>(block);
            }
            _plansChanged.RevalidatePendingPlans();
            return OperationResult<UserScriptBinding>.Ok(existing!);
        }
        catch (Exception ex)
        {
            return Internal<UserScriptBinding>(ex);
        }
    }

    /// <summary>编辑事务提交后的输入绑定写入；编辑会话自身持有对应准入租约。</summary>

    internal OperationResult<UserScriptBinding> CommitPendingConfigInput(
        string userId,
        string scriptId,
        string name,
        string value)
    {
        if (string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(value)
            || name.Length > 128
            || value.Length > 512
            || name.Any(character => !char.IsLetterOrDigit(character) && character != '_')
            || value.Any(char.IsControl))
        {
            return Validation<UserScriptBinding>("配置输入值格式无效");
        }
        try
        {
            UserScriptBinding? result = null;
            string? error = null;
            _admission.WithCoordination(() =>
            {
                _state.Mutate(state =>
                {
                    NexusUser? user = state.Users.FirstOrDefault(item =>
                        string.Equals(item.Id, userId, StringComparison.OrdinalIgnoreCase));
                    UserScriptBinding? binding = user?.Bindings.FirstOrDefault(item =>
                        string.Equals(item.ScriptInstanceId, scriptId, StringComparison.Ordinal));
                    if (user is null || binding is null)
                    {
                        error = "用户绑定不存在";
                        return;
                    }
                    Dictionary<string, string> oldInputs = new(binding.ConfigInputs, StringComparer.OrdinalIgnoreCase);
                    binding.ConfigInputs[name] = value;
                    try
                    {
                        UserDefinitionStore.SaveUsers(state.Users);
                        result = binding.Clone();
                    }
                    catch
                    {
                        binding.ConfigInputs = oldInputs;
                        throw;
                    }
                });
            });
            return error is not null
                ? NotFound<UserScriptBinding>(error)
                : result is null
                    ? Internal<UserScriptBinding>(new InvalidOperationException("配置输入绑定提交未产生结果"))
                    : OperationResult<UserScriptBinding>.Ok(result);
        }
        catch (Exception ex)
        {
            return Internal<UserScriptBinding>(ex);
        }
    }

    public OperationResult<bool> DeleteBinding(
        string userId,
        string scriptId,
        string source = Audit.Web)
    {
        NexusUser? user = _state.Find(userId);
        UserScriptBinding? binding = user?.Bindings.FirstOrDefault(item => item.ScriptInstanceId == scriptId);
        if (user is null || binding is null)
        {
            return NotFound<bool>("用户绑定不存在");
        }
        string? error = null;
        UserMutationBlock? block = null;
        try
        {
            _admission.WithCoordination(() =>
            {
                block = _policy.CheckBinding(user.Id, scriptId);
                if (block is not null)
                {
                    return;
                }
                _state.Mutate(state =>
                {
                    NexusUser? currentUser = state.Users.FirstOrDefault(item =>
                        string.Equals(item.Id, user.Id, StringComparison.OrdinalIgnoreCase));
                    UserScriptBinding? currentBinding = currentUser?.Bindings.FirstOrDefault(item =>
                        string.Equals(item.ScriptInstanceId, scriptId, StringComparison.Ordinal));
                    int index = currentUser is null || currentBinding is null
                        ? -1
                        : currentUser.Bindings.IndexOf(currentBinding);
                    if (currentUser is null || currentBinding is null || index < 0)
                    {
                        error = "绑定不存在";
                        return;
                    }
                    currentUser.Bindings.RemoveAt(index);
                    try
                    {
                        UserDefinitionStore.SaveUsers(state.Users);
                        _configData.RemoveUserScriptData(currentUser.Id, scriptId);
                    }
                    catch
                    {
                        currentUser.Bindings.Insert(index, currentBinding);
                        try { UserDefinitionStore.SaveUsers(state.Users); } catch { }
                        throw;
                    }
                });
            });
            if (error is not null)
            {
                return Conflict<bool>("resource_busy", error);
            }
            if (block is not null)
            {
                return MutationConflict<bool>(block);
            }
            _plansChanged.RevalidatePendingPlans();
            Audit.Log(source, "解除全局用户脚本绑定", $"{user.Name} / {scriptId}");
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
    }

    private static UserScriptBinding NormalizeBinding(
        UserScriptBinding candidate,
        string scriptId,
        IReadOnlyDictionary<string, string>? configInputs = null)
    {
        return new UserScriptBinding
        {
            ScriptInstanceId = scriptId.Trim(),
            Enabled = candidate.Enabled,
            ConfigInputs = new Dictionary<string, string>(
                configInputs ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase),
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

}
