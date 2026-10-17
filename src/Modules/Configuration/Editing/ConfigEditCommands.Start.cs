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
    public OperationResult<ConfigEditStarted> Start(
        string scriptId,
        string userReference,
        string mode = "normal",
        string source = Audit.Web,
        IReadOnlyDictionary<string, string>? inputOverrides = null)
    {
        OperationResult<ConfigEditTarget> targetResult = ResolveTarget(scriptId, userReference);
        if (!targetResult.Succeeded)
        {
            return OperationResult<ConfigEditStarted>.Failure(targetResult.Error!);
        }

        ConfigEditTarget target = targetResult.Value!;
        string? pluginError = PluginAvailability.GetUnavailableReason(
            target.Script,
            _pluginAvailability);
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
        bool hasSnapshot = ConfigSnapshotService.HasSnapshot(target.Script.Id, target.UserKey);
        if (editMode != "normal" && hasSnapshot)
        {
            // 快照已存在（前端状态过期或并发编辑后）：按既有快照交换流程执行，保持数据一致。
            editMode = "normal";
        }
        ConfigEditPendingInput? pendingInput = null;
        if (inputOverrides is not null)
        {
            if (editMode != "reuse")
            {
                return Validation<ConfigEditStarted>("配置候选覆盖仅支持复用配置编辑");
            }
            if (!TryCreatePendingInput(inputOverrides, out pendingInput, out string? inputError))
            {
                return Validation<ConfigEditStarted>(inputError ?? "配置输入值格式无效");
            }
            ConfigEditPendingInput selectedInput = pendingInput!;

            // 候选配置只在编辑会话内覆盖解析结果；用户绑定要等编辑成功保存后再提交。
            var overrides = new Dictionary<string, string>(
                target.User.Binding.ConfigInputs,
                StringComparer.OrdinalIgnoreCase)
            {
                [selectedInput.Name] = selectedInput.Value,
            };
            targetResult = ResolveTarget(scriptId, userReference, overrides);
            if (!targetResult.Succeeded)
            {
                return OperationResult<ConfigEditStarted>.Failure(targetResult.Error!);
            }
            target = targetResult.Value!;
        }
        if (editMode == "fresh" && target.Spec?.ConfigEdit?.FreshInput is ConfigEditFreshInput freshInput)
        {
            // freshInput 是插件声明的稳定目标；输入绑定在提交成功后才落盘。
            // 只有首次编辑仍处于 fresh 时才覆盖用户旧绑定，避免过期入口把已有快照重绑定到新目标。
            var overrides = new Dictionary<string, string>(
                target.User.Binding.ConfigInputs,
                StringComparer.OrdinalIgnoreCase)
            {
                [freshInput.Name] = freshInput.Value,
            };
            targetResult = ResolveTarget(scriptId, userReference, overrides);
            if (!targetResult.Succeeded)
            {
                return OperationResult<ConfigEditStarted>.Failure(targetResult.Error!);
            }
            target = targetResult.Value!;
            pendingInput = new ConfigEditPendingInput { Name = freshInput.Name, Value = freshInput.Value };
        }
        if (editMode == "fresh"
            && !string.IsNullOrWhiteSpace(target.Script.PluginType)
            && _capabilities.HasCapability(target.Script.PluginType, PluginCapabilityKeys.NoFreshConfig))
        {
            // 插件声明脚本没有生成全新配置文件的能力（配置由目标软件自建），全新编辑入口不可用。
            return Validation<ConfigEditStarted>("因脚本配置限制，无法生成全新配置。请使用「复用配置文件」编辑现有配置");
        }
        if (editMode == "normal" && !hasSnapshot)
        {
            return Validation<ConfigEditStarted>("首次编辑请先选择配置方式：全新配置文件或复用配置文件");
        }
        if (editMode != "fresh"
            && target.Spec is { } specForCandidates
            && (specForCandidates.ConfigInputCandidates.Count >= 2
                || specForCandidates.RequiredConfigRelativePath.Length > 0 && specForCandidates.ConfigInputValue.Length == 0))
        {
            // configPath 模板的绑定输入未定且存在多个候选（含目录型 configPath——残缺时解析为存在的
            // 目录，直接准备会把整个目录采用为用户快照）：先让用户选定接管目标，仅选中项进入快照。
            return OperationResult<ConfigEditStarted>.Failure(
                new OperationError(
                    "config_input_mismatch",
                    "请先选择要接管的原生配置实例",
                    OperationErrorKind.Validation,
                    (_capabilities.GetMissingConfigCandidateSet(target.Script.PluginType, target.Script.RootPath, null)?.Values
                        ?? specForCandidates.ConfigInputCandidates).ToList(),
                    specForCandidates.ConfigInputName));
        }
        if (editMode == "reuse" && PathKindUtil.KindOf(target.Script.ConfigPath) == PathKind.Missing)
        {
            // 复用编辑把现场配置文件绑定为用户快照起点：声明位置缺失（常见于文件型配置的配置名输入
            // 与现场实际文件名不一致）必须在启动会话前解决，否则编辑完成后无法入库、甚至把同名默认
            // 文件静默错绑。候选为静态目录中的实际配置，交给用户显式选择后更新用户绑定输入。
            ConfigInputCandidateSet? candidateSet = _capabilities.GetMissingConfigCandidateSet(
                target.Script.PluginType,
                target.Script.RootPath,
                target.Script.PluginInputs);
            IReadOnlyList<string> candidates = candidateSet?.Values
                ?? _capabilities.GetMissingConfigCandidates(
                    target.Script.PluginType,
                    target.Script.RootPath,
                    target.Script.PluginInputs);
            string message = candidates.Count > 0
                ? "配置文件不存在：" + target.Script.ConfigPath + "。请选择要复用的现场配置文件，或先在用户绑定中更新配置输入"
                : "配置文件不存在：" + target.Script.ConfigPath + "。请检查脚本根目录与配置名设置（可能已在目标软件中改名或删除）";
            return OperationResult<ConfigEditStarted>.Failure(
                new OperationError(
                    "config_input_mismatch",
                    message,
                    OperationErrorKind.Validation,
                    candidates,
                    candidateSet?.InputName));
        }

        ConfigSessionRuntimeMetadata metadata = ConfigSessionMark.FromScript(
            target.Script,
            target.Spec?.ProfileHash ?? "",
            target.Spec?.PluginVersion ?? "",
            target.Spec?.ExtraConfigPaths) with { RejectImplicitRebind = target.Spec?.RequireExclusiveProcess == true, ConfigContractId = target.Spec?.ConfigContractId ?? "" };
        ConfigEditPreparationOptions? preparationOptions = null;
        bool isolateCandidates = target.Spec?.ConfigEdit?.IsolateSiblingCandidates == true;
        if (editMode == "reuse"
            && (target.Spec?.ConfigInputCandidatePaths.Count ?? 0) < 2)
        {
            // 单候选 reuse 在目标目录原位编辑；只有多候选 reuse 才需要把选中项复制成工作副本。
            isolateCandidates = false;
        }
        if (pendingInput is null
            && editMode == "reuse"
            && !hasSnapshot
            && target.Spec is { ConfigInputName: { Length: > 0 } inputName, ConfigInputValue: { Length: > 0 } inputValue }
            && (!target.User.Binding.ConfigInputs.TryGetValue(inputName, out string? boundValue)
                || !string.Equals(boundValue.Trim(), inputValue, StringComparison.OrdinalIgnoreCase)))
        {
            // 单候选会由插件自动采用；同样延迟到成功保存后再把自动采用的输入写入用户绑定。
            pendingInput = new ConfigEditPendingInput { Name = inputName, Value = inputValue };
        }
        if ((isolateCandidates || pendingInput is not null) && target.Spec is { } specForEdit)
        {
            preparationOptions = new ConfigEditPreparationOptions(
                isolateCandidates,
                isolateCandidates
                    ? specForEdit.ConfigInputCandidatePaths
                        .Append(target.Script.ConfigPath)
                        .ToArray()
                    : Array.Empty<string>(),
                pendingInput);
        }

        ScriptConfigGate.Lease? gate = ScriptConfigGate.Get(target.Script.Id);
        bool gateAcquired = false;
        bool gateBusy = false;
        bool editLeaseHeld = false;
        string? editLeaseConflict = null;
        IReadOnlyList<ExecutionLeaseReference> leases;
        string? failureCode;
        bool changed;
        try
        {
            changed = _admission.TryExecuteLeaseMutation(
                target.Script.Id,
                target.UserKey,
                () =>
                {
                    if (!gate!.Wait(0))
                    {
                        gateBusy = true;
                        return;
                    }

                    gateAcquired = true;
                    if (!_admission.TryBeginEditSession(
                            target.Script.Id,
                            target.UserKey,
                            target.Script.ConfigPath,
                            out editLeaseConflict))
                    {
                        gate!.Release();
                        gateAcquired = false;
                        return;
                    }

                    editLeaseHeld = true;
                },
                out leases,
                out failureCode);
        }
        catch
        {
            if (editLeaseHeld)
            {
                _admission.EndEditSession(target.Script.Id, target.UserKey);
                editLeaseHeld = false;
            }

            if (gateAcquired)
            {
                gate!.Release();
                gateAcquired = false;
            }

            gate.Dispose();
            throw;
        }
        if (!changed)
        {
            if (gateAcquired)
            {
                gate!.Release();
                gateAcquired = false;
            }
            gate?.Dispose();
            return LeaseConflict<ConfigEditStarted>(leases, $"user:{target.Script.Id}:{target.UserKey}", failureCode);
        }

        if (gateBusy || !gateAcquired || editLeaseConflict is not null)
        {
            if (gateAcquired)
            {
                gate!.Release();
                gateAcquired = false;
            }
            gate?.Dispose();
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
            if (!ExecutablePathRules.IsExecutable(target.Script.MainExe))
            {
                return Validation<ConfigEditStarted>("脚本主程序路径错误或不是可执行文件");
            }

            if ((target.Spec?.RequireExclusiveProcess == true
                    && RuntimeImageActivityProbe.ObserveExecutable(target.Script.MainExe) != "inactive")
                || SystemActions.IsExeRunning(target.Script.MainExe)
                || SystemActions.IsExeRunning(ResolveLaunchTargetExe(target.Script)))
            {
                return Conflict<ConfigEditStarted>(
                    "resource_busy",
                    "检测到已打开的脚本，退出脚本后才能编辑配置。");
            }

            string? prepError = editMode switch
            {
                "fresh" => ConfigEditSessionService.PrepareForEditFresh(
                    target.Script.Id,
                    target.UserKey,
                    target.Script.ConfigPath,
                    metadata,
                    target.Spec?.ExtraConfigPaths,
                    preparationOptions),
                "reuse" => ConfigEditSessionService.PrepareForEditReuse(
                    target.Script.Id,
                    target.UserKey,
                    target.Script.ConfigPath,
                    metadata,
                    target.Spec?.ExtraConfigPaths,
                    preparationOptions),
                _ => ConfigEditSessionService.PrepareForEdit(
                    target.Script.Id,
                    target.UserKey,
                    target.Script.ConfigPath,
                    metadata,
                    target.Spec?.ExtraConfigPaths,
                    preparationOptions),
            };
            if (prepError is not null)
            {
                ConfigWorkDirMaintenance.SweepIdleWorkDir(target.Script.Id, target.UserKey);
                return Validation<ConfigEditStarted>("配置交换失败：" + prepError);
            }

            // 交换/准备动作已在服务层写入会话标记；此处只把正常运行标记转换成编辑标记。
            ConfigSessionMark? preparedMark = ConfigSessionMark.TryRead(target.Script.Id, target.UserKey);
            if (preparedMark is null)
            {
                return Validation<ConfigEditStarted>("配置交换已准备但缺少会话标记");
            }
            preparedMark.SessionPhase = "edit";
            preparedMark.EditMode = editMode;
            preparedMark.Write();
            ConfigSessionMark editMark = preparedMark;
            if (target.Spec?.ConfigEditor is ConfigEditorDescriptor editor)
            {
                ConfigValidationResult editorResult = ConfigEditPreparationScriptRunner.Execute(
                    editor,
                    target.Script,
                    target.User,
                    editMark,
                    editMode,
                    target.Spec?.ConfigInputName ?? "",
                    target.Spec?.ConfigInputValue ?? "");
                if (!string.IsNullOrWhiteSpace(editorResult.Error))
                {
                    string? rollbackError = ConfigEditSessionService.CancelEdit(
                        target.Script.Id,
                        target.UserKey,
                        target.Script.ConfigPath,
                        target.Spec?.ExtraConfigPaths);
                    ConfigWorkDirMaintenance.SweepIdleWorkDir(target.Script.Id, target.UserKey);
                    return Validation<ConfigEditStarted>(
                        "execution_failed",
                        "配置编辑准备脚本失败：" + editorResult.Error
                        + (rollbackError is null ? "，配置已还原" : "，配置还原失败：" + rollbackError));
                }
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
                    ConfigEditSessionService.CancelEdit(
                        target.Script.Id,
                        target.UserKey,
                        target.Script.ConfigPath,
                        target.Spec?.ExtraConfigPaths);
                }
                finally
                {
                    ConfigWorkDirMaintenance.SweepIdleWorkDir(target.Script.Id, target.UserKey);
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
                Mark = editMark,
                Spec = target.Spec,
                ConfigGate = gate,
            };
            gate = null;
            gateAcquired = false;
            pendingSession = session;
            ConfigEditSessionRegistry.EditSessions[target.Script.Id] = session;
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
                    if (ConfigEditSessionRegistry.EditSessions.TryGetValue(
                            target.Script.Id,
                            out EditSession? registered))
                    {
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

                        ConfigEditSessionService.CancelEdit(
                            target.Script.Id,
                            target.UserKey,
                            target.Script.ConfigPath,
                            target.Spec?.ExtraConfigPaths);
                        ConfigWorkDirMaintenance.SweepIdleWorkDir(target.Script.Id, target.UserKey);
                        ConfigEditSessionRegistry.EditSessions.TryRemove(target.Script.Id, out _);
                        _admission.EndEditSession(target.Script.Id, target.UserKey);
                        registered.DisposeProcessResources();
                        editLeaseHeld = false;
                        gateAcquired = false;
                    }
                    else if (startedProcess is not null)
                    {
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
                        ConfigEditSessionService.CancelEdit(
                            target.Script.Id,
                            target.UserKey,
                            target.Script.ConfigPath,
                            target.Spec?.ExtraConfigPaths);
                        ConfigWorkDirMaintenance.SweepIdleWorkDir(target.Script.Id, target.UserKey);
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
                    _admission.EndEditSession(target.Script.Id, target.UserKey);
                    editLeaseHeld = false;
                }

                if (gateAcquired)
                {
                    gate!.Release();
                    gateAcquired = false;
                }
            }
            gate?.Dispose();
        }
    }
}
