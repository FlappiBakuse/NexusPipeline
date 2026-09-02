using System.Diagnostics;
using NexusPipeline.App.Abstractions;
using NexusPipeline.App.Contracts;
using NexusPipeline.Models;
using NexusPipeline.Plugins;
using NexusPipeline.Services;
using NexusPipeline.Services.Configuration;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.App.Commands;

/// <summary>配置编辑生命周期的应用命令结果。</summary>
internal sealed record ConfigEditStarted(int ProcessId, string EditMode);

/// <summary>完成配置编辑后的提交结果；validator feedback 随同本次 API 响应返回。</summary>
internal sealed record ConfigEditCompleted(bool Success, ConfigValidationResult Validation);

/// <summary>
/// 配置编辑生命周期应用命令。
/// Web、CLI 和交互菜单只负责协议适配；配置交换、进程控制、租约与会话门禁统一在常驻服务内执行。
/// </summary>
internal static class ConfigEditCommands
{
    public static OperationResult<ConfigEditStarted> Start(
        RuntimeContext ctx,
        string scriptId,
        string userReference,
        string mode = "normal",
        string source = Audit.Web)
    {
        OperationResult<ConfigEditTarget> targetResult = ResolveTarget(ctx, scriptId, userReference);
        if (!targetResult.Succeeded)
        {
            return OperationResult<ConfigEditStarted>.Failure(targetResult.Error!);
        }

        ConfigEditTarget target = targetResult.Value!;
        string? pluginError = PluginAvailability.GetUnavailableReason(
            target.Script,
            ctx.Resolve<IPluginAvailability>());
        if (pluginError is not null)
        {
            return Validation<ConfigEditStarted>(pluginError);
        }
        if (string.IsNullOrWhiteSpace(target.Script.ConfigPath))
        {
            return Validation<ConfigEditStarted>("脚本未配置「配置文件路径/文件夹」");
        }

        string? editMode = NormalizeEditMode(mode);
        if (editMode is null)
        {
            return Validation<ConfigEditStarted>("未知的编辑方式：" + mode + "（支持 fresh / reuse / normal）");
        }
        bool hasSnapshot = UserConfigManager.HasSnapshot(target.Script.Id, target.UserKey);
        if (editMode == "normal" && !hasSnapshot)
        {
            return Validation<ConfigEditStarted>("首次编辑请先选择配置方式：全新配置文件或复用配置文件");
        }
        if (editMode != "normal" && hasSnapshot)
        {
            // 快照已存在（前端状态过期或并发编辑后）：按既有快照交换流程执行，保持数据一致。
            editMode = "normal";
        }

        ConfigSessionRuntimeMetadata metadata = ConfigSessionMark.FromScript(
            target.Script,
            target.Spec?.ProfileHash ?? "",
            target.Spec?.PluginVersion ?? "");

        SemaphoreSlim gate = ScriptConfigGate.Get(target.Script.Id);
        bool gateAcquired = false;
        bool gateBusy = false;
        bool editLeaseHeld = false;
        string? editLeaseConflict = null;
        bool changed = ctx.Center.TryExecuteLeaseMutation(
            target.Script.Id,
            target.UserKey,
            () =>
            {
                if (!gate.Wait(0))
                {
                    gateBusy = true;
                    return;
                }

                gateAcquired = true;
                if (!ctx.Center.TryBeginEditSession(
                        target.Script.Id,
                        target.UserKey,
                        target.Script.ConfigPath,
                        out editLeaseConflict))
                {
                    gate.Release();
                    gateAcquired = false;
                    return;
                }

                editLeaseHeld = true;
            },
            out IReadOnlyList<ExecutionLeaseReference> leases,
            out string? failureCode);
        if (!changed)
        {
            return LeaseConflict<ConfigEditStarted>(leases, $"user:{target.Script.Id}:{target.UserKey}", failureCode);
        }

        if (gateBusy || !gateAcquired || editLeaseConflict is not null)
        {
            return Conflict<ConfigEditStarted>(
                "resource_busy",
                editLeaseConflict ?? "脚本正在运行或编辑配置中");
        }

        bool keepGate = false;
        try
        {
            if (!TextRules.IsExecutable(target.Script.MainExe))
            {
                return Validation<ConfigEditStarted>("脚本主程序路径错误或不是可执行文件");
            }

            if (SystemActions.IsExeRunning(target.Script.MainExe)
                || SystemActions.IsExeRunning(ResolveLaunchTargetExe(target.Script)))
            {
                return Conflict<ConfigEditStarted>(
                    "resource_busy",
                    "检测到已打开的脚本，退出脚本后才能编辑配置。");
            }

            UserConfigManager.RestoreHiddenConfigs(
                target.Script.Id,
                target.UserKey,
                target.Script.ConfigPath);
            string? prepError = editMode switch
            {
                "fresh" => UserConfigManager.PrepareForEditFresh(
                    target.Script.Id, target.UserKey, target.Script.ConfigPath, metadata),
                "reuse" => UserConfigManager.PrepareForEditReuse(
                    target.Script.Id, target.UserKey, target.Script.ConfigPath, metadata),
                _ => UserConfigManager.PrepareForEdit(
                    target.Script.Id, target.UserKey, target.Script.ConfigPath, metadata),
            };
            if (prepError is not null)
            {
                return Validation<ConfigEditStarted>("配置交换失败：" + prepError);
            }

            // 交换/准备动作已在服务层写入会话标记；此处统一把 Phase 收敛为 edit 并记录实际编辑模式。
            var editMark = new ConfigSessionMark
            {
                ScriptId = target.Script.Id,
                UserName = target.UserKey,
                UserId = target.UserKey,
                ConfigPath = target.Script.ConfigPath,
                OriginalKind = ConfigSessionMark.TryRead(target.Script.Id, target.UserKey)?.OriginalKind
                    ?? PathKindUtil.Text(PathKindUtil.KindOf(target.Script.ConfigPath)),
                Phase = "edit",
                SessionPhase = "edit",
                EditMode = editMode,
            };
            editMark.WorkingDirectory = metadata.WorkingDirectory;
            editMark.LaunchExe = metadata.LaunchExe;
            editMark.ProcessIdentity = metadata.ProcessIdentity;
            editMark.ProfileHash = metadata.ProfileHash;
            editMark.PluginName = metadata.PluginName;
            editMark.PluginVersion = metadata.PluginVersion;
            editMark.ConfigKind = metadata.ConfigKind;
            editMark.Write();
            if (editMode != "reuse")
            {
                // reuse 语义为「无任何文件动作」：不隐藏 config 同目录的其他配置文件。
                UserConfigManager.HideOtherConfigs(target.Script, target.Script.Id, target.UserKey);
            }

            Process? process;
            try
            {
                process = SystemActions.StartVisible(
                    target.Script.MainExe,
                    string.IsNullOrWhiteSpace(target.Script.RootPath)
                        ? Path.GetDirectoryName(target.Script.MainExe) ?? ""
                        : target.Script.RootPath);
            }
            catch (Exception ex)
            {
                try
                {
                    UserConfigManager.CancelEdit(
                        target.Script.Id,
                        target.UserKey,
                        target.Script.ConfigPath);
                }
                finally
                {
                    // 主程序启动失败时 CancelEdit 只负责配置交换回滚；同目录隐藏配置也必须立即恢复。
                    UserConfigManager.RestoreHiddenConfigs(
                        target.Script.Id,
                        target.UserKey,
                        target.Script.ConfigPath);
                }
                return Validation<ConfigEditStarted>(
                    "execution_failed",
                    "主程序启动失败：" + ex.Message + "，配置已还原，可修正后重试");
            }

            SystemActions.BringToFrontFireAndForget(process?.Id ?? 0, "编辑配置");
            UserConfigManager.EditSessions[target.Script.Id] = new EditSession
            {
                Script = target.Script,
                User = target.User,
                Process = process,
                Mark = editMark,
                Spec = target.Spec,
            };
            keepGate = true;
            Audit.Log(source, "开始编辑配置", $"{target.Script.Name} / {target.User.UserName}（主程序已启动，方式={editMode}）");
            return OperationResult<ConfigEditStarted>.Ok(new ConfigEditStarted(process?.Id ?? 0, editMode));
        }
        catch (Exception ex)
        {
            if (keepGate)
            {
                try
                {
                    if (UserConfigManager.EditSessions.TryGetValue(
                            target.Script.Id,
                            out EditSession? registered))
                    {
                        if (registered.Process is not null)
                        {
                            SystemActions.KillOwnedProcessTree(
                                null,
                                registered.Process.Id,
                                ResolveLaunchTargetExe(target.Script),
                                "脚本",
                                stableSeconds: 3);
                        }

                        UserConfigManager.CancelEdit(
                            target.Script.Id,
                            target.UserKey,
                            target.Script.ConfigPath);
                        UserConfigManager.RestoreHiddenConfigs(
                            target.Script.Id,
                            target.UserKey,
                            target.Script.ConfigPath);
                        UserConfigManager.EditSessions.TryRemove(target.Script.Id, out _);
                        ctx.Center.EndEditSession(target.Script.Id, target.UserKey);
                        editLeaseHeld = false;
                    }
                }
                catch (Exception cleanupEx)
                {
                    Logger.Error($"[错误] 编辑配置会话异常后的现场清理失败（交由自愈兜底）：{cleanupEx.Message}");
                }

                keepGate = false;
            }

            return Internal<ConfigEditStarted>(ex);
        }
        finally
        {
            if (!keepGate)
            {
                if (editLeaseHeld)
                {
                    ctx.Center.EndEditSession(target.Script.Id, target.UserKey);
                    editLeaseHeld = false;
                }

                if (gateAcquired)
                {
                    gate.Release();
                }
            }
        }
    }

    public static OperationResult<ConfigEditCompleted> Complete(
        RuntimeContext ctx,
        string scriptId,
        string userReference,
        string action,
        string source = Audit.Web)
    {
        if (action is not ("done" or "cancel"))
        {
            return Validation<ConfigEditCompleted>("未知操作：" + action);
        }

        if (!UserConfigManager.EditSessions.TryGetValue(
                scriptId,
                out EditSession? session))
        {
            return Conflict<ConfigEditCompleted>("resource_busy", "没有进行中的编辑配置会话");
        }

        if (!IsSessionUser(session.User, userReference))
        {
            return NotFound<ConfigEditCompleted>("用户绑定不存在");
        }
        // 完成/取消沿用开始编辑时冻结的脚本、用户和 validator；当前插件 profile 变化不影响收尾路径。
        ConfigEditTarget target = new(
            session.Script,
            session.User,
            ResolveSessionUserKey(session),
            session.Spec);
        string sessionUserKey = string.IsNullOrWhiteSpace(session.Mark.UserId)
            ? target.UserKey
            : session.Mark.UserId;

        SemaphoreSlim gate = ScriptConfigGate.Get(target.Script.Id);
        bool sessionRemoved = false;
        try
        {
            string editMode = NormalizeEditMode(session.Mark?.EditMode ?? "normal") ?? "normal";
            if (action == "done"
                && editMode != "normal"
                && !ConfigLocationHasContent(session.Script.ConfigPath))
            {
                // fresh/reuse 提交时 config 位置为空（脚本未生成或配置被删）：不杀进程，保留会话供用户继续配置或取消。
                return Validation<ConfigEditCompleted>("配置文件尚未生成，请先完成配置或取消本次编辑");
            }

            string launchExe = ResolveLaunchTargetExe(session.Script);
            bool processClean = session.Process is not null
                ? SystemActions.KillOwnedProcessTree(
                    null,
                    session.Process.Id,
                    launchExe,
                    "脚本",
                    stableSeconds: 3)
                : SystemActions.KillExistingProcessesByIdentity(
                    launchExe,
                    "脚本",
                    stableSeconds: 3);
            if (!processClean)
            {
                return Conflict<ConfigEditCompleted>(
                    "resource_busy",
                    "脚本程序无法完全退出（可能持续自重启），请先在托盘退出脚本后重试");
            }

            string? swapError = action == "done"
                ? UserConfigManager.CommitEdit(
                    session.Script.Id,
                    sessionUserKey,
                    session.Script.ConfigPath)
                : UserConfigManager.CancelEdit(
                    session.Script.Id,
                    sessionUserKey,
                    session.Script.ConfigPath);
            if (swapError is not null)
            {
                return Validation<ConfigEditCompleted>(
                    "execution_failed",
                    (action == "done" ? "提交" : "取消") + "失败：" + swapError);
            }

            UserConfigManager.RestoreHiddenConfigs(
                session.Script.Id,
                sessionUserKey,
                session.Script.ConfigPath);

            ConfigValidationResult validation = action == "done"
                ? RunConfigValidator(session.Script, session.User, sessionUserKey, session.Spec?.ConfigValidator)
                : ConfigValidationResult.Skipped;

            sessionRemoved = UserConfigManager.EditSessions.TryRemove(target.Script.Id, out _);
            if (sessionRemoved)
            {
                ctx.Center.EndEditSession(session.Script.Id, sessionUserKey);
            }

            Audit.Log(
                source,
                action == "done" ? "完成编辑配置" : "取消编辑配置",
                $"{target.Script.Name} / {target.User.UserName}");
            return OperationResult<ConfigEditCompleted>.Ok(new ConfigEditCompleted(true, validation));
        }
        catch (Exception ex)
        {
            return Internal<ConfigEditCompleted>(ex);
        }
        finally
        {
            if (sessionRemoved)
            {
                gate.Release();
            }
        }
    }

    private static ConfigValidationResult RunConfigValidator(
        ScriptInstance script,
        ResolvedScriptUser user,
        string userKey,
        ConfigValidatorDescriptor? resolvedDescriptor)
    {
        if (string.IsNullOrWhiteSpace(script.PluginType) || resolvedDescriptor is null)
        {
            return ConfigValidationResult.Skipped;
        }

        string storeRoot = UserConfigManager.StoreDir(script.Id, userKey);
        try
        {
            return ConfigValidationScriptRunner.ExecuteAsync(
                    resolvedDescriptor,
                    script,
                    user,
                    storeRoot)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            // Runner 内部已捕获脚本错误；这里兜住插件查询/编排层异常，确保提交结果保持成功。
            string error = "JavaScript 执行失败：" + ex.Message;
            Logger.Warn($"[专项配置校验:{script.PluginType}] {error}");
            return new ConfigValidationResult(
                true,
                error,
                Array.Empty<string>(),
                Array.Empty<ConfigValidationToast>(),
                Array.Empty<ConfigValidationNotification>());
        }
    }

    private static OperationResult<ConfigEditTarget> ResolveTarget(
        RuntimeContext ctx,
        string scriptId,
        string userReference)
    {
        ScriptInstance? declaration = ctx.FindScript(scriptId);
        if (declaration is null)
        {
            return NotFound<ConfigEditTarget>($"未找到脚本实例：{scriptId}");
        }
        ResolvedScriptSpec spec = ctx.ResolveScriptSpec(declaration);
        if (!spec.Succeeded)
        {
            return Validation<ConfigEditTarget>(spec.Error ?? "脚本有效配置解析失败");
        }
        ScriptInstance script = spec.Script;

        NexusUser? globalUser = ctx.FindUser(userReference);
        ResolvedScriptUser? user = null;
        lock (ctx.DataLock)
        {
            globalUser ??= ctx.Users.FirstOrDefault(item =>
                string.Equals(item.Name, userReference, StringComparison.OrdinalIgnoreCase));
            if (globalUser is not null)
            {
                UserScriptBinding? binding = globalUser.Bindings.FirstOrDefault(item =>
                    string.Equals(item.ScriptInstanceId, script.Id, StringComparison.Ordinal));
                user = binding is null
                    ? null
                    : new ResolvedScriptUser(
                        globalUser.Id,
                        globalUser.Name,
                        binding.Clone());
            }
        }

        if (globalUser is null || user is null)
        {
            return NotFound<ConfigEditTarget>("用户绑定不存在");
        }

        return OperationResult<ConfigEditTarget>.Ok(
            new ConfigEditTarget(script, user, globalUser.Id, spec));
    }

    private static string ResolveLaunchTargetExe(ScriptInstance script)
    {
        string workingDir = string.IsNullOrWhiteSpace(script.RootPath)
            ? Path.GetDirectoryName(script.MainExe) ?? ""
            : script.RootPath;
        return SystemActions.ResolveLaunchTarget(script.MainExe, workingDir, script.Args).ExePath;
    }

    private static bool IsSessionUser(ResolvedScriptUser user, string userReference)
    {
        return !string.IsNullOrWhiteSpace(userReference)
            && (string.Equals(user.UserId, userReference, StringComparison.OrdinalIgnoreCase)
                || string.Equals(user.UserName, userReference, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveSessionUserKey(EditSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.Mark.UserId))
        {
            return session.Mark.UserId;
        }
        if (!string.IsNullOrWhiteSpace(session.User.UserId))
        {
            return session.User.UserId;
        }
        return session.Mark.UserName;
    }

    /// <summary>归一化编辑方式：空值视为 normal；非法值返回 null。</summary>
    private static string? NormalizeEditMode(string? mode)
    {
        string value = (mode ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "" or "normal" => "normal",
            "fresh" => "fresh",
            "reuse" => "reuse",
            _ => null,
        };
    }

    /// <summary>config 位置是否存在可入库的配置内容（文件存在，或目录非空）。</summary>
    private static bool ConfigLocationHasContent(string configPath)
    {
        PathKind kind = PathKindUtil.KindOf(configPath);
        if (kind == PathKind.Missing)
        {
            return false;
        }
        if (kind == PathKind.File)
        {
            return true;
        }
        return Directory.Exists(configPath) && Directory.EnumerateFileSystemEntries(configPath).Any();
    }

    private static OperationResult<T> Validation<T>(
        string message,
        string code = "validation_error") =>
        OperationResult<T>.Failure(code, message, OperationErrorKind.Validation);

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
        OperationResult<T>.Failure(
            "internal_error",
            exception.Message,
            OperationErrorKind.Internal);

    private sealed record ConfigEditTarget(
        ScriptInstance Script,
        ResolvedScriptUser User,
        string UserKey,
        ResolvedScriptSpec? Spec = null);
}
