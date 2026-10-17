using System.Diagnostics;
using System.Security.Cryptography;
using NexusPipeline.Modules.Configuration.Contracts;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Configuration.Validation;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts.Resolution;
using NexusPipeline.Modules.Scripts.Validation;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users.UseCases;
using NexusPipeline.Modules.Users;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Results;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Settings;

namespace NexusPipeline.Modules.Configuration.Editing;

internal sealed partial class ConfigEditCommands
{
    public OperationResult<ConfigEditCompleted> Complete(
        string scriptId,
        string userReference,
        string action,
        string source = Audit.Web)
    {
        if (action is not ("done" or "cancel"))
        {
            return Validation<ConfigEditCompleted>("未知操作：" + action);
        }

        if (!ConfigEditSessionRegistry.EditSessions.TryGetValue(
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

            if (action == "done" && session.Spec?.TaskProtocol?.Version is "0.2.0" or "0.3.0"
                && session.Spec.ConfigEditor is ConfigEditorDescriptor commitEditor)
            {
                var commitMark = session.Mark ?? throw new InvalidOperationException("配置编辑会话标记缺失");
                var result = ConfigEditPreparationScriptRunner.Execute(commitEditor, session.Script, session.User,
                    commitMark, editMode, session.Spec.ConfigInputName, session.Spec.ConfigInputValue,
                    trigger: "config-edit-commit");
                if (!string.IsNullOrWhiteSpace(result.Error))
                    return Validation<ConfigEditCompleted>("execution_failed", "配置编辑提交脚本失败：" + result.Error);
            }

            Stopwatch swapTimer = Stopwatch.StartNew();
            string? swapError = action == "done"
                ? ConfigEditSessionService.CommitEdit(
                    session.Script.Id,
                    sessionUserKey,
                    session.Script.ConfigPath,
                    session.Spec?.ExtraConfigPaths)
                : ConfigEditSessionService.CancelEdit(
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

            EditSession activeSession = session ?? throw new InvalidOperationException("编辑会话已在收尾期间消失");
            if (action == "done" && activeSession.Mark.PendingConfigInput is ConfigEditPendingInput pendingInput)
            {
                OperationResult<UserScriptBinding> bindingResult = _userCommands.CommitPendingConfigInput(
                    sessionUserKey,
                    session.Script.Id,
                    pendingInput.Name,
                    pendingInput.Value);
                if (!bindingResult.Succeeded)
                {
                    return Validation<ConfigEditCompleted>(
                        "execution_failed",
                        "配置输入绑定提交失败：" + (bindingResult.ErrorMessage ?? "未知错误"));
                }
                ConfigSessionMark.Clear(session.Script.Id, sessionUserKey);
            }

            Stopwatch validatorTimer = Stopwatch.StartNew();
            ConfigValidationResult validation = action == "done"
                ? RunConfigAssessment(session.Script, session.User, session.Spec)
                : ConfigValidationResult.Skipped;
            Logger.Info($"[配置编辑] validator 耗时 {validatorTimer.ElapsedMilliseconds} ms（操作={action}）。");

            sessionRemoved = ConfigEditSessionRegistry.EditSessions.TryRemove(target.Script.Id, out _);
            if (sessionRemoved)
            {
                _admission.EndEditSession(session.Script.Id, sessionUserKey);
                session.DisposeProcessResources();
                ConfigWorkDirMaintenance.SweepIdleWorkDir(session.Script.Id, sessionUserKey);
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
            Logger.Info($"[配置编辑] {action} 收尾总耗时 {totalTimer.ElapsedMilliseconds} ms（脚本={scriptId}）。");
        }
    }

    internal ConfigValidationResult RunConfigAssessment(
        ScriptInstance script,
        ResolvedScriptUser user,
        ResolvedScriptSpec? spec)
    {
        if (spec?.TaskProtocol?.Version is "0.2.0" or "0.3.0" && _taskProtocolAssessment is not null)
        {
            try
            {
                TaskPlan? plan = _taskProtocolAssessment.RunAsync(
                        spec,
                        user,
                        "config-edit")
                    .GetAwaiter()
                    .GetResult();
                return new ConfigValidationResult(
                    true,
                    "",
                    Array.Empty<string>(),
                    Array.Empty<ConfigValidationToast>(),
                    Array.Empty<ConfigValidationNotification>())
                {
                    Diagnostics = plan is null
                        ? Array.Empty<ConfigValidationDiagnostic>()
                        : _taskProtocolAssessment.ToDiagnostics(plan, user),
                };
            }
            catch (Exception ex)
            {
                // Assessment is advisory after a successful configuration commit;
                // never turn a saved configuration into a failed edit response.
                Logger.Warn($"[任务协议配置校验:{script.PluginType}] 校验失败（不阻断保存）：{ex.Message}");
                return new ConfigValidationResult(
                    true,
                    "",
                    Array.Empty<string>(),
                    Array.Empty<ConfigValidationToast>(),
                    Array.Empty<ConfigValidationNotification>())
                {
                    Diagnostics = spec is null
                        ? Array.Empty<ConfigValidationDiagnostic>()
                        : new[] { _taskProtocolAssessment.Error(spec, user, ex.Message) },
                };
            }
        }

        return ConfigValidationResult.Skipped;
    }
}
