using System.Diagnostics;
using NexusPipeline.App.Abstractions;
using NexusPipeline.App.Contracts;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.App.Commands;

/// <summary>配置编辑生命周期的应用命令结果。</summary>
internal sealed record ConfigEditStarted(int ProcessId);

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
            string? prepError = UserConfigManager.PrepareForEdit(
                target.Script.Id,
                target.UserKey,
                target.Script.ConfigPath);
            if (prepError is not null)
            {
                return Validation<ConfigEditStarted>("配置交换失败：" + prepError);
            }

            List<string> generatedTemplateFiles = UserConfigManager.EnsureConfigForEdit(
                target.Script,
                ctx.Resolve<IPluginCapabilityResolver>());
            bool generatedTemplate = generatedTemplateFiles.Count > 0;
            var editMark = new ConfigSessionMark
            {
                ScriptId = target.Script.Id,
                UserName = target.UserKey,
                ConfigPath = target.Script.ConfigPath,
                OriginalKind = ConfigSessionMark.TryRead(target.Script.Id, target.UserKey)?.OriginalKind
                    ?? PathKindUtil.Text(PathKindUtil.KindOf(target.Script.ConfigPath)),
                Phase = "edit",
                GeneratedTemplate = generatedTemplate,
                TemplateFiles = generatedTemplateFiles,
            };
            editMark.Write();
            UserConfigManager.HideOtherConfigs(target.Script, target.Script.Id, target.UserKey);

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
                UserConfigManager.CancelEdit(
                    target.Script.Id,
                    target.UserKey,
                    target.Script.ConfigPath);
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
                GeneratedConfigTemplate = generatedTemplate,
                Mark = editMark,
            };
            keepGate = true;
            Audit.Log(source, "开始编辑配置", $"{target.Script.Name} / {target.User.UserName}（主程序已启动）");
            return OperationResult<ConfigEditStarted>.Ok(new ConfigEditStarted(process?.Id ?? 0));
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

    public static OperationResult<bool> Complete(
        RuntimeContext ctx,
        string scriptId,
        string userReference,
        string action,
        string source = Audit.Web)
    {
        if (action is not ("done" or "cancel"))
        {
            return Validation<bool>("未知操作：" + action);
        }

        OperationResult<ConfigEditTarget> targetResult = ResolveTarget(ctx, scriptId, userReference);
        if (!targetResult.Succeeded)
        {
            return OperationResult<bool>.Failure(targetResult.Error!);
        }

        ConfigEditTarget target = targetResult.Value!;
        if (!UserConfigManager.EditSessions.TryGetValue(
                target.Script.Id,
                out EditSession? session))
        {
            return Conflict<bool>("resource_busy", "没有进行中的编辑配置会话");
        }

        SemaphoreSlim gate = ScriptConfigGate.Get(target.Script.Id);
        bool sessionRemoved = false;
        try
        {
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
                return Conflict<bool>(
                    "resource_busy",
                    "脚本程序无法完全退出（可能持续自重启），请先在托盘退出脚本后重试");
            }

            string? swapError = action == "done"
                ? UserConfigManager.CommitEdit(
                    target.Script.Id,
                    target.UserKey,
                    target.Script.ConfigPath)
                : UserConfigManager.CancelEdit(
                    target.Script.Id,
                    target.UserKey,
                    target.Script.ConfigPath);
            if (swapError is not null)
            {
                return Validation<bool>(
                    "execution_failed",
                    (action == "done" ? "提交" : "取消") + "失败：" + swapError);
            }

            if (action == "cancel" && session.GeneratedConfigTemplate)
            {
                DeleteGeneratedTemplateFiles(session.Mark);
            }

            UserConfigManager.RestoreHiddenConfigs(
                target.Script.Id,
                target.UserKey,
                target.Script.ConfigPath);
            sessionRemoved = UserConfigManager.EditSessions.TryRemove(target.Script.Id, out _);
            if (sessionRemoved)
            {
                ctx.Center.EndEditSession(target.Script.Id, target.UserKey);
            }

            Audit.Log(
                source,
                action == "done" ? "完成编辑配置" : "取消编辑配置",
                $"{target.Script.Name} / {target.User.UserName}");
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
        finally
        {
            if (sessionRemoved)
            {
                gate.Release();
            }
        }
    }

    private static OperationResult<ConfigEditTarget> ResolveTarget(
        RuntimeContext ctx,
        string scriptId,
        string userReference)
    {
        ScriptInstance? script = ctx.FindScript(scriptId);
        if (script is null)
        {
            return NotFound<ConfigEditTarget>($"未找到脚本实例：{scriptId}");
        }

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
            new ConfigEditTarget(script, user, globalUser.Id));
    }

    private static string ResolveLaunchTargetExe(ScriptInstance script)
    {
        string workingDir = string.IsNullOrWhiteSpace(script.RootPath)
            ? Path.GetDirectoryName(script.MainExe) ?? ""
            : script.RootPath;
        return SystemActions.ResolveLaunchTarget(script.MainExe, workingDir, script.Args).ExePath;
    }

    private static void DeleteGeneratedTemplateFiles(ConfigSessionMark mark)
    {
        if (mark.TemplateFiles.Count > 0)
        {
            string? baseDir = Path.GetDirectoryName(mark.ConfigPath);
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                return;
            }

            foreach (string relativePath in mark.TemplateFiles)
            {
                try
                {
                    string destination = Path.Combine(baseDir, relativePath);
                    if (File.Exists(destination))
                    {
                        File.Delete(destination);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(
                        $"[警告] 清理编辑会话生成的配置模板失败：{relativePath}（{ex.Message}）");
                }
            }

            return;
        }

        try
        {
            if (File.Exists(mark.ConfigPath))
            {
                File.Delete(mark.ConfigPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 清理编辑会话生成的配置模板失败：{ex.Message}");
        }
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
        string UserKey);
}
