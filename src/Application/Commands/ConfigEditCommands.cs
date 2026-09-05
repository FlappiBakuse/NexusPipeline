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
        if (editMode == "fresh"
            && !string.IsNullOrWhiteSpace(target.Script.PluginType)
            && ctx.Resolve<PluginManager>().HasCapability(target.Script.PluginType, Extensibility.PluginCapabilityKeys.NoFreshConfig))
        {
            // 插件声明脚本没有生成全新配置文件的能力（配置由目标软件自建），全新编辑入口不可用。
            return Validation<ConfigEditStarted>("因脚本配置限制，无法生成全新配置。请使用「复用配置文件」编辑现有配置");
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
        if (target.Spec is { } specForCandidates && specForCandidates.ConfigInputCandidates.Count >= 2)
        {
            // configPath 模板的绑定输入未定且存在多个候选（含目录型 configPath——残缺时解析为存在的
            // 目录，直接准备会把整个目录采用为用户快照）：先让用户选定接管目标，仅选中项进入快照。
            return OperationResult<ConfigEditStarted>.Failure(
                new OperationError(
                    "config_input_mismatch",
                    "当前脚本目录存在多个配置，请先选择要接管的配置",
                    OperationErrorKind.Validation,
                    specForCandidates.ConfigInputCandidates.ToList()));
        }
        if (editMode == "reuse" && PathKindUtil.KindOf(target.Script.ConfigPath) == PathKind.Missing)
        {
            // 复用编辑把现场配置文件绑定为用户快照起点：声明位置缺失（常见于文件型配置的配置名输入
            // 与现场实际文件名不一致）必须在启动会话前解决，否则编辑完成后无法入库、甚至把同名默认
            // 文件静默错绑。候选为静态目录中的实际配置，交给用户显式选择后更新脚本实例配置名。
            IReadOnlyList<string> candidates = ctx.Resolve<IPluginCapabilityResolver>()
                .GetMissingConfigCandidates(target.Script.PluginType, target.Script.RootPath, target.Script.PluginInputs);
            string message = candidates.Count > 0
                ? "配置文件不存在：" + target.Script.ConfigPath + "。请选择要复用的现场配置文件，或先在脚本实例中更新配置名设置"
                : "配置文件不存在：" + target.Script.ConfigPath + "。请检查脚本根目录与配置名设置（可能已在目标软件中改名或删除）";
            return OperationResult<ConfigEditStarted>.Failure(
                new OperationError("config_input_mismatch", message, OperationErrorKind.Validation, candidates));
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
        Process? startedProcess = null;
        ProcessIdentity? startedIdentity = null;
        ProcessOwnership? startedOwnership = null;
        EditSession? pendingSession = null;
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
                // fresh/reuse 模式的「全新/复用」只针对主配置；附加配置路径保持现场，提交时差异入库。
                "fresh" => UserConfigManager.PrepareForEditFresh(
                    target.Script.Id, target.UserKey, target.Script.ConfigPath, metadata),
                "reuse" => UserConfigManager.PrepareForEditReuse(
                    target.Script.Id, target.UserKey, target.Script.ConfigPath, metadata),
                _ => UserConfigManager.PrepareForEdit(
                    target.Script.Id, target.UserKey, target.Script.ConfigPath, metadata, target.Spec?.ExtraConfigPaths),
            };
            if (prepError is not null)
            {
                return Validation<ConfigEditStarted>("配置交换失败：" + prepError);
            }

            // 交换/准备动作已在服务层写入会话标记；此处统一记录实际编辑模式。
            ConfigSessionMark? preparedMark = ConfigSessionMark.TryRead(target.Script.Id, target.UserKey);
            var editMark = new ConfigSessionMark
            {
                ScriptId = target.Script.Id,
                UserId = target.UserKey,
                ConfigPath = target.Script.ConfigPath,
                SessionPhase = "edit",
                EditMode = editMode,
            };
            editMark.WorkingDirectory = metadata.WorkingDirectory;
            editMark.LaunchExe = metadata.LaunchExe;
            editMark.ProcessIdentity = metadata.ProcessIdentity;
            editMark.ProfileHash = metadata.ProfileHash;
            editMark.PluginName = metadata.PluginName;
            editMark.PluginVersion = metadata.PluginVersion;
            editMark.ConfigKind = preparedMark?.ConfigKind ?? metadata.ConfigKind;
            editMark.Write();
            if (editMode != "reuse")
            {
                // reuse 语义为「无任何文件动作」：不隐藏 config 同目录的其他配置文件。
                UserConfigManager.HideOtherConfigs(target.Script, target.Script.Id, target.UserKey);
            }

            Process? process;
            try
            {
                startedOwnership = ProcessOwnership.TryCreate("编辑配置");
                process = SystemActions.StartVisible(
                    target.Script.MainExe,
                    string.IsNullOrWhiteSpace(target.Script.RootPath)
                        ? Path.GetDirectoryName(target.Script.MainExe) ?? ""
                        : target.Script.RootPath,
                    startedOwnership);
                if (process is null)
                {
                    throw new InvalidOperationException("主程序没有返回有效进程句柄");
                }
                startedProcess = process;
                startedIdentity = ProcessIdentity.Capture(process);
            }
            catch (Exception ex)
            {
                startedOwnership?.Dispose();
                startedOwnership = null;
                try
                {
                    UserConfigManager.CancelEdit(
                        target.Script.Id,
                        target.UserKey,
                        target.Script.ConfigPath,
                        target.Spec?.ExtraConfigPaths);
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

            ProcessOwnership? sessionOwnership = startedOwnership?.HasAssignedProcess == true
                ? startedOwnership
                : null;
            if (sessionOwnership is null)
            {
                startedOwnership?.Dispose();
                startedOwnership = null;
            }
            var session = new EditSession
            {
                Script = target.Script,
                User = target.User,
                Process = process,
                ProcessIdentity = startedIdentity,
                ProcessOwnership = sessionOwnership,
                ForegroundCancellation = startedIdentity is null ? null : new CancellationTokenSource(),
                Mark = editMark,
                Spec = target.Spec,
            };
            pendingSession = session;
            if (startedIdentity is not null && session.ForegroundCancellation is not null)
            {
                // 先创建并持有前置任务，再把会话公开给 Complete；避免极短编辑窗口在任务赋值前完成收尾。
                session.ForegroundTask = SystemActions.BringToFrontAsync(
                    startedIdentity.Value,
                    "编辑配置",
                    session.ForegroundCancellation.Token,
                    stopAfterFirstAttempt: true);
                EventHandler processExited = (_, _) => session.CancelForeground();
                session.ProcessExitedHandler = processExited;
                try
                {
                    process.Exited += processExited;
                    process.EnableRaisingEvents = true;
                    if (process.HasExited)
                    {
                        session.CancelForeground();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[配置编辑] 注册进程退出通知失败，将由身份轮询结束前置任务：{ex.Message}");
                }
            }
            UserConfigManager.EditSessions[target.Script.Id] = session;
            pendingSession = null;
            startedOwnership = null;
            startedProcess = null;
            keepGate = true;
            Audit.Log(source, "开始编辑配置", $"{target.Script.Name} / {target.User.UserName}（主程序已启动，方式={editMode}）");
            return OperationResult<ConfigEditStarted>.Ok(new ConfigEditStarted(process.Id, editMode));
        }
        catch (Exception ex)
        {
            if (keepGate || startedProcess is not null)
            {
                try
                {
                    if (UserConfigManager.EditSessions.TryGetValue(
                            target.Script.Id,
                            out EditSession? registered))
                    {
                        registered.CancelForeground();
                        if (registered.Process is not null)
                        {
                            SystemActions.KillEditProcess(
                                registered.ProcessOwnership,
                                registered.ProcessIdentity,
                                registered.Process.Id,
                                ResolveLaunchTargetExe(target.Script),
                                "编辑配置",
                                stableSeconds: 3);
                        }

                        UserConfigManager.CancelEdit(
                            target.Script.Id,
                            target.UserKey,
                            target.Script.ConfigPath,
                            target.Spec?.ExtraConfigPaths);
                        UserConfigManager.RestoreHiddenConfigs(
                            target.Script.Id,
                            target.UserKey,
                            target.Script.ConfigPath);
                        UserConfigManager.EditSessions.TryRemove(target.Script.Id, out _);
                        ctx.Center.EndEditSession(target.Script.Id, target.UserKey);
                        registered.DisposeProcessResources();
                        editLeaseHeld = false;
                    }
                    else if (startedProcess is not null)
                    {
                        pendingSession?.CancelForeground();
                        SystemActions.KillEditProcess(
                            startedOwnership,
                            startedIdentity,
                            startedProcess.Id,
                            ResolveLaunchTargetExe(target.Script),
                            "编辑配置",
                            stableSeconds: 3);
                        pendingSession?.DisposeProcessResources();
                        pendingSession = null;
                        startedOwnership?.Dispose();
                        startedOwnership = null;
                        startedProcess.Dispose();
                        startedProcess = null;
                        UserConfigManager.CancelEdit(
                            target.Script.Id,
                            target.UserKey,
                            target.Script.ConfigPath,
                            target.Spec?.ExtraConfigPaths);
                        UserConfigManager.RestoreHiddenConfigs(
                            target.Script.Id,
                            target.UserKey,
                            target.Script.ConfigPath);
                    }
                }
                catch (Exception cleanupEx)
                {
                    Logger.Error($"[错误] 编辑配置会话异常后的现场清理失败（交由自愈兜底）：{cleanupEx.Message}");
                }

                keepGate = false;
            }

            startedOwnership?.Dispose();
            startedProcess?.Dispose();

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
        Stopwatch totalTimer = Stopwatch.StartNew();
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
            if (action == "done"
                && editMode == "normal"
                && PathKindUtil.KindOf(session.Script.ConfigPath) == PathKind.Missing)
            {
                // normal 提交时 config 位置缺失（文件型配置常因目标软件中改名/移动而消失，目录型同理）：
                // 无法安全计算编辑差异，不杀进程，保留会话供用户恢复原文件后重试；取消会把编辑前原文件还原回该位置。
                return Validation<ConfigEditCompleted>(
                    "配置文件不存在（可能已在目标软件中被改名或删除）。可在目标软件中恢复原文件名后重试保存，或取消本次编辑，再在脚本实例中更新配置名设置");
            }

            session.CancelForeground();
            string launchExe = ResolveLaunchTargetExe(session.Script);
            Stopwatch cleanupTimer = Stopwatch.StartNew();
            bool processClean = session.Process is not null
                ? SystemActions.KillEditProcess(
                    session.ProcessOwnership,
                    session.ProcessIdentity,
                    session.Process.Id,
                    launchExe,
                    "编辑配置",
                    stableSeconds: 3)
                : SystemActions.KillExistingProcessesByIdentity(
                    launchExe,
                    "编辑配置",
                    stableSeconds: 3);
            Logger.Info($"[配置编辑] 进程收尾耗时 {cleanupTimer.ElapsedMilliseconds} ms（{target.Script.Id} / {sessionUserKey}）。");
            if (!processClean)
            {
                return Conflict<ConfigEditCompleted>(
                    "resource_busy",
                    "脚本程序无法完全退出（可能持续自重启），请先在托盘退出脚本后重试");
            }

            Stopwatch swapTimer = Stopwatch.StartNew();
            string? swapError = action == "done"
                ? UserConfigManager.CommitEdit(
                    session.Script.Id,
                    sessionUserKey,
                    session.Script.ConfigPath,
                    session.Spec?.ExtraConfigPaths)
                : UserConfigManager.CancelEdit(
                    session.Script.Id,
                    sessionUserKey,
                    session.Script.ConfigPath,
                    session.Spec?.ExtraConfigPaths);
            Logger.Info($"[配置编辑] 配置交换耗时 {swapTimer.ElapsedMilliseconds} ms（操作={action}）。");
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

            Stopwatch validatorTimer = Stopwatch.StartNew();
            ConfigValidationResult validation = action == "done"
                ? RunConfigValidator(session.Script, session.User, sessionUserKey, session.Spec)
                : ConfigValidationResult.Skipped;
            Logger.Info($"[配置编辑] validator 耗时 {validatorTimer.ElapsedMilliseconds} ms（操作={action}）。");

            sessionRemoved = UserConfigManager.EditSessions.TryRemove(target.Script.Id, out _);
            if (sessionRemoved)
            {
                ctx.Center.EndEditSession(session.Script.Id, sessionUserKey);
                session.DisposeProcessResources();
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
            Logger.Info($"[配置编辑] {action} 收尾总耗时 {totalTimer.ElapsedMilliseconds} ms（脚本={scriptId}）。");
        }
    }

    private static ConfigValidationResult RunConfigValidator(
        ScriptInstance script,
        ResolvedScriptUser user,
        string userKey,
        ResolvedScriptSpec? spec)
    {
        if (string.IsNullOrWhiteSpace(script.PluginType) || spec?.ConfigValidator is null)
        {
            return ConfigValidationResult.Skipped;
        }

        string storeRoot = UserConfigManager.StoreDir(script.Id, userKey);
        try
        {
            return ConfigValidationScriptRunner.ExecuteAsync(
                    spec.ConfigValidator,
                    script,
                    user,
                    storeRoot,
                    "config-edit",
                    ScriptSaveValidation.BuildExtraSnapshots(script.Id, userKey, spec.ExtraConfigPaths))
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
        ScriptInstance? declaration = ctx.EntityState.FindScript(scriptId);
        if (declaration is null)
        {
            return NotFound<ConfigEditTarget>($"未找到脚本实例：{scriptId}");
        }
        ResolvedScriptUser? binding = ctx.Resolve<IUserRepository>()
            .ResolveBinding(declaration, userReference);
        if (binding is null)
        {
            return NotFound<ConfigEditTarget>("用户绑定不存在");
        }
        // 专项快照按用户绑定输入实例化：接管哪个配置文件/实例目录是用户级选择
        ResolvedScriptSpec spec = ctx.Resolve<ScriptSpecResolver>().Resolve(declaration, binding.Binding.ConfigInputs);
        if (!spec.Succeeded)
        {
            return Validation<ConfigEditTarget>(spec.Error ?? "脚本有效配置解析失败");
        }
        ScriptInstance script = spec.Script;

        return OperationResult<ConfigEditTarget>.Ok(
            new ConfigEditTarget(script, binding, binding.UserId, spec));
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
        return "";
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
