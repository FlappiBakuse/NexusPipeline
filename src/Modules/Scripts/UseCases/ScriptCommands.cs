using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts.Resolution;
using NexusPipeline.Modules.Scripts.Validation;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Settings.Validation;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Naming;
using NexusPipeline.Shared.Results;
using NexusPipeline.Modules.Scripts.Persistence;

namespace NexusPipeline.Modules.Scripts.UseCases;

/// <summary>
/// 脚本实例的应用命令。HTTP、CLI 和交互菜单都通过常驻服务进入这里，Web 层只负责协议解析与展示投影。
/// 图标提取属于 Web 展示适配，不放入本命令。
/// </summary>
internal sealed class ScriptCommands
{
    private readonly IScriptRepository _scripts;
    private readonly IScriptMutationState _state;
    private readonly IScriptDeletionTransaction _deletion;
    private readonly IScriptMutationAdmission _admission;
    private readonly IScriptConfigGate _configGate;
    private readonly IPluginAvailability _pluginAvailability;
    private readonly IPluginCapabilityResolver _pluginCapabilities;
    private readonly ScriptSpecResolver _resolver;
    private readonly IScriptPlansChanged _plansChanged;

    internal ScriptCommands(
        IScriptRepository scripts,
        IScriptMutationState state,
        IScriptDeletionTransaction deletion,
        IScriptMutationAdmission admission,
        IScriptConfigGate configGate,
        IPluginAvailability pluginAvailability,
        IPluginCapabilityResolver pluginCapabilities,
        ScriptSpecResolver resolver,
        IScriptPlansChanged plansChanged)
    {
        _scripts = scripts;
        _state = state;
        _deletion = deletion;
        _admission = admission;
        _configGate = configGate;
        _pluginAvailability = pluginAvailability;
        _pluginCapabilities = pluginCapabilities;
        _resolver = resolver;
        _plansChanged = plansChanged;
    }

    public OperationResult<ScriptInstance> Create(ScriptInstance candidate, string source = Audit.Web)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                return Validation<ScriptInstance>("script_name_required", "脚本名称不能为空");
            }

            NormalizePaths(candidate);
            // 候选脚本在解析判断脚本资产前必须拥有最终实例 ID；新建请求中的空 ID 不能参与文件名生成。
            candidate.Id = Guid.NewGuid().ToString("N");
            ResolvedScriptSpec? resolvedCandidate = ResolveCandidate(candidate, out string? pluginError);
            if (pluginError is not null || resolvedCandidate is null)
            {
                return Validation<ScriptInstance>(pluginError ?? "专用插件配置解析失败");
            }
            ScriptInstance effectiveCandidate = resolvedCandidate.Script;
            if (effectiveCandidate.JudgeScriptEnabled && string.IsNullOrWhiteSpace(effectiveCandidate.JudgeScript))
            {
                return Validation<ScriptInstance>("开启「使用判断脚本」但判断脚本代码为空");
            }
            string? pathError = ScriptPathPolicy.CheckScriptPaths(
                effectiveCandidate,
                _pluginCapabilities);
            if (pathError is not null)
            {
                return Validation<ScriptInstance>(pathError);
            }

            string? limitError = null;
            bool duplicateName = false;
            _state.Mutate(scripts =>
            {
                limitError = Limits.CheckScriptCount(scripts.Count)
                    ?? Limits.CheckNameBytes(candidate.Name, AppFixedLimits.MaxEntityNameBytes, "脚本名称");
                if (limitError is null && EntityNameRules.HasConflict(scripts, candidate.Name, script => script.Name))
                {
                    duplicateName = true;
                    limitError = "脚本名称重复：脚本实例已存在同名脚本";
                }
                limitError ??= Limits.CheckAttempts(candidate.MaxAttempts)
                    ?? Limits.CheckScriptTimeouts(candidate.LogStallTimeoutMinutes, candidate.TotalTimeoutMinutes);
                if (limitError is null)
                {
                    candidate.Index = scripts.Count == 0 ? 0 : scripts.Max(item => item.Index) + 1;
                    scripts.Add(candidate);
                    try
                    {
                        ScriptDefinitionStore.SaveScripts(scripts.ToList());
                    }
                    catch
                    {
                        scripts.Remove(candidate);
                        throw;
                    }
                }
            });
            if (limitError is not null)
            {
                return duplicateName
                    ? Conflict<ScriptInstance>("duplicate_name", limitError)
                    : Validation<ScriptInstance>(limitError);
            }

            _plansChanged.RevalidatePendingPlans();
            Audit.Log(source, "添加脚本实例", $"{candidate.Name}（id={candidate.Id}）");
            return OperationResult<ScriptInstance>.Ok(
                _resolver.ResolveScript(candidate));
        }
        catch (Exception ex)
        {
            return Internal<ScriptInstance>(ex);
        }
    }

    public OperationResult<ScriptInstance> Update(
        string scriptId,
        ScriptInstance candidate,
        string source = Audit.Web)
    {
        ScriptInstance? existing = _scripts.FindById(scriptId);
        if (existing is null)
        {
            return NotFound<ScriptInstance>($"未找到脚本实例：{scriptId}");
        }

        string? existingPluginError = PluginAvailability.GetUnavailableReason(
            existing,
            _pluginAvailability);
        if (existingPluginError is not null)
        {
            return Validation<ScriptInstance>(existingPluginError);
        }

        IDisposable? gate = _configGate.TryAcquire(existing.Id);
        if (gate is null)
        {
            return Conflict<ScriptInstance>(
                "resource_busy",
                "脚本正在运行或编辑配置中，无法修改");
        }
        try
        {
            string? limitError = Limits.CheckNameBytes(candidate.Name, AppFixedLimits.MaxEntityNameBytes, "脚本名称")
                ?? Limits.CheckAttempts(candidate.MaxAttempts)
                ?? Limits.CheckScriptTimeouts(candidate.LogStallTimeoutMinutes, candidate.TotalTimeoutMinutes);
            if (limitError is not null)
            {
                return Validation<ScriptInstance>(limitError);
            }

            candidate.Id = existing.Id;
            candidate.Index = existing.Index;
            NormalizePaths(candidate);
            ResolvedScriptSpec? resolvedCandidate = ResolveCandidate(candidate, out string? pluginError);
            if (pluginError is not null || resolvedCandidate is null)
            {
                return Validation<ScriptInstance>(pluginError ?? "专用插件配置解析失败");
            }
            ScriptInstance effectiveCandidate = resolvedCandidate.Script;
            if (effectiveCandidate.JudgeScriptEnabled && string.IsNullOrWhiteSpace(effectiveCandidate.JudgeScript))
            {
                return Validation<ScriptInstance>("开启「使用判断脚本」但判断脚本代码为空");
            }
            string? pathError = ScriptPathPolicy.CheckScriptPaths(
                effectiveCandidate,
                _pluginCapabilities);
            if (pathError is not null)
            {
                return Validation<ScriptInstance>(pathError);
            }

            ScriptInstance? previous = null;
            int previousIndex = -1;
            string? mutationError = null;
            bool duplicateName = false;
            ScriptMutationAdmissionResult admission = _admission.TryExecute(
                existing.Id,
                null,
                () =>
                {
                    _state.Mutate(scripts =>
                    {
                        int index = scripts.ToList().FindIndex(script =>
                            string.Equals(script.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
                        if (index < 0)
                        {
                            mutationError = $"未找到脚本实例：{scriptId}";
                            return;
                        }
                        if (EntityNameRules.HasConflict(
                                scripts,
                                candidate.Name,
                                script => script.Name,
                                script => string.Equals(script.Id, existing.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            duplicateName = true;
                            mutationError = "脚本名称重复：脚本实例已存在同名脚本";
                            return;
                        }
                        previousIndex = index;
                        previous = scripts[index].Clone();
                        scripts[index] = candidate.Clone();
                        try
                        {
                            ScriptDefinitionStore.SaveScripts(scripts.ToList());
                        }
                        catch
                        {
                            // 保存失败时撤销内存替换，避免运行态已经切换到未落盘的候选配置。
                            if (previous is not null && previousIndex >= 0 && previousIndex < scripts.Count)
                            {
                                scripts[previousIndex] = previous;
                            }
                            throw;
                        }
                    });
                });
            if (!admission.Allowed)
            {
                return LeaseConflict<ScriptInstance>(admission.RunIds, $"script:{existing.Id}", admission.FailureCode);
            }
            if (mutationError is not null)
            {
                return duplicateName
                    ? Conflict<ScriptInstance>("duplicate_name", mutationError)
                    : Validation<ScriptInstance>(mutationError);
            }

            _plansChanged.RevalidatePendingPlans();
            Audit.Log(source, "修改脚本实例", $"{candidate.Name}（id={candidate.Id}）");
            return OperationResult<ScriptInstance>.Ok(
                _resolver.ResolveScript(candidate));
        }
        catch (Exception ex)
        {
            return Internal<ScriptInstance>(ex);
        }
        finally
        {
            gate.Dispose();
        }
    }

    /// <summary>删除不存在的 ID 仍返回成功，保持既有 Web API 的幂等语义。</summary>
    public OperationResult<ScriptInstance?> Delete(string scriptId, string source = Audit.Web)
    {
        try
        {
            ScriptDeletionResult result = _deletion.Execute(scriptId, source, out string? failureCode);
            if (!result.Allowed)
            {
                return LeaseConflict<ScriptInstance?>(
                    result.RunIds,
                    $"script:{scriptId}",
                    failureCode ?? result.FailureCode);
            }

            return OperationResult<ScriptInstance?>.Ok(result.Removed);
        }
        catch (Exception ex)
        {
            return Internal<ScriptInstance?>(ex);
        }
    }

    public OperationResult<bool> Reorder(IReadOnlyList<string>? ids, string source = Audit.Web)
    {
        try
        {
            string? error = null;
            _state.Mutate(scripts =>
            {
                if (ids is null || ids.Count != scripts.Count
                    || ids.Any(string.IsNullOrWhiteSpace)
                    || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
                {
                    error = "脚本顺序名单缺失或与当前脚本列表不一致";
                }
                else
                {
                    HashSet<string> existing = new(scripts.Select(script => script.Id), StringComparer.Ordinal);
                    if (ids.Any(id => !existing.Contains(id)))
                    {
                        error = "脚本顺序名单与当前脚本列表不一致";
                    }
                    else
                    {
                        Dictionary<string, ScriptInstance> byId = scripts.ToDictionary(script => script.Id, StringComparer.Ordinal);
                        Dictionary<string, int> oldIndexes = scripts.ToDictionary(
                            script => script.Id,
                            script => script.Index,
                            StringComparer.Ordinal);
                        for (int i = 0; i < ids.Count; i++)
                        {
                            byId[ids[i]].Index = i;
                        }
                        try
                        {
                        ScriptDefinitionStore.SaveScripts(scripts.ToList());
                        }
                        catch
                        {
                            foreach (ScriptInstance script in scripts)
                            {
                                script.Index = oldIndexes[script.Id];
                            }
                            throw;
                        }
                    }
                }
            });
            if (error is not null)
            {
                return Validation<bool>(error);
            }
            _plansChanged.RevalidatePendingPlans();
            Audit.Log(source, "调整脚本顺序", $"{ids!.Count} 个脚本实例");
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
    }

    public OperationResult<ScriptProfile> Probe(string pluginType, string rootPath, IReadOnlyDictionary<string, string>? inputs = null)
    {
        if (string.IsNullOrWhiteSpace(pluginType))
        {
            return Validation<ScriptProfile>("缺少专用插件标识");
        }
        string? availabilityError = PluginAvailability.GetUnavailableReason(
            pluginType,
            _pluginAvailability);
        if (availabilityError is not null)
        {
            return Validation<ScriptProfile>(availabilityError);
        }
        ScriptProfile? profile = _pluginCapabilities
            .ResolveProfile(pluginType, StripPathQuotes(rootPath), inputs);
        return profile is null
            ? Validation<ScriptProfile>("无法从脚本根目录推导专用插件配置（请检查根目录、插件输入与专用插件启用状态）")
            : OperationResult<ScriptProfile>.Ok(profile);
    }

    private ResolvedScriptSpec? ResolveCandidate(
        ScriptInstance candidate,
        out string? error)
    {
        error = null;
        ResolvedScriptSpec resolved = _resolver.ResolveCandidate(candidate);
        if (!resolved.Succeeded)
        {
            error = resolved.Error ?? (string.IsNullOrWhiteSpace(candidate.PluginType)
                ? "通用判断脚本资产不存在或不可读"
                : "专用插件无法从脚本根目录推导配置（请检查脚本根目录，并确认专用插件已启用）");
        }
        return resolved;
    }

    private static void NormalizePaths(ScriptInstance script)
    {
        script.RootPath = StripPathQuotes(script.RootPath);
        script.MainExe = StripPathQuotes(script.MainExe);
        script.ConfigPath = StripPathQuotes(script.ConfigPath);
        script.LogPath = StripPathQuotes(script.LogPath);
        script.GameExe = StripPathQuotes(script.GameExe);
    }

    private static string StripPathQuotes(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
        string trimmed = value.Trim();
        if (trimmed.Length >= 2)
        {
            char first = trimmed[0];
            char last = trimmed[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                return trimmed[1..^1].Trim();
            }
        }
        return trimmed;
    }

    private static OperationResult<T> Validation<T>(string message) =>
        OperationResult<T>.Failure("validation_error", message, OperationErrorKind.Validation);

    private static OperationResult<T> Validation<T>(
        string code,
        string message,
        IReadOnlyDictionary<string, object?>? messageArgs = null) =>
        OperationResult<T>.Failure(
            code,
            message,
            OperationErrorKind.Validation,
            messageKey: $"api.error.{code}",
            messageArgs: messageArgs);

    private static OperationResult<T> NotFound<T>(string message) =>
        OperationResult<T>.Failure("not_found", message, OperationErrorKind.NotFound);

    private static OperationResult<T> Conflict<T>(string code, string message) =>
        OperationResult<T>.Failure(code, message, OperationErrorKind.Conflict);

    private static OperationResult<T> LeaseConflict<T>(
        IReadOnlyList<string> runIds,
        string resource,
        string? failureCode = null)
    {
        if (failureCode == "host_maintenance")
        {
            return OperationResult<T>.Failure(
                "host_maintenance",
                "宿主正在进行维护操作，暂不能修改运行配置",
                OperationErrorKind.Conflict);
        }
        return OperationResult<T>.Failure(
            "execution_resource_in_use",
            $"执行计划正在引用资源「{resource}」，当前无法修改；请等待相关运行结束",
            OperationErrorKind.Conflict,
            runIds.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static OperationResult<T> Internal<T>(Exception exception) =>
        OperationResult<T>.Failure("internal_error", exception.Message, OperationErrorKind.Internal);

}
