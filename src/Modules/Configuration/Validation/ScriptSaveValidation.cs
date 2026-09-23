using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts.Resolution;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Modules.Configuration.Validation;

/// <summary>
/// 保存脚本实例后的专项配置校验（script-save 语境）：对每个绑定用户以其 store 为根运行插件的
/// 统一运行 taskProtocol 配置诊断与兼容旧 configValidator（只读比较 + 通知），
/// 聚合结果供 Web 响应返回。保存结果不受校验影响；无可用快照或无绑定用户时返回 null。
/// </summary>
internal sealed class ScriptSaveValidation
{
    private static readonly TimeSpan TaskProtocolAssessmentBudget = TimeSpan.FromSeconds(30);
    private readonly ScriptSpecResolver _resolver;
    private readonly IUserSnapshotReader _users;
    private readonly ITaskProtocolConfigAssessmentPort? _taskProtocolAssessment;

    internal ScriptSaveValidation(
        ScriptSpecResolver resolver,
        IUserSnapshotReader users,
        ITaskProtocolConfigAssessmentPort? taskProtocolAssessment = null)
    {
        _resolver = resolver;
        _users = users;
        _taskProtocolAssessment = taskProtocolAssessment;
    }

    public async Task<ConfigValidationResult?> RunForScriptAsync(ScriptInstance script)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(script.PluginType))
            {
                return null;
            }
            ResolvedScriptSpec sharedSpec = _resolver.Resolve(script);
            if (!sharedSpec.Succeeded)
            {
                return null;
            }
            List<ResolvedScriptUser> targets = _users.Snapshot()
                .Select(user => (User: user, Binding: user.Bindings.FirstOrDefault(binding =>
                    string.Equals(binding.ScriptInstanceId, script.Id, StringComparison.Ordinal))))
                .Where(item => item.Binding is not null)
                .Select(item => new ResolvedScriptUser(item.User.Id, item.User.Name, item.Binding!))
                .ToList();
            if (targets.Count == 0)
            {
                return null;
            }

            var toasts = new List<ConfigValidationToast>();
            var notifications = new List<ConfigValidationNotification>();
            var diagnostics = new List<ConfigValidationDiagnostic>();
            using var assessmentBudget = new CancellationTokenSource(TaskProtocolAssessmentBudget);
            HashSet<string> toastKeys = new(StringComparer.Ordinal);
            HashSet<string> notificationKeys = new(StringComparer.Ordinal);
            string? error = null;
            bool ran = false;
            foreach (ResolvedScriptUser user in targets)
            {
                // 按用户绑定输入实例化校验语境：接管配置是用户级选择
                ResolvedScriptSpec spec = user.Binding.ConfigInputs.Count == 0
                    ? sharedSpec
                    : _resolver.Resolve(script, user.Binding.ConfigInputs);
                if (!spec.Succeeded)
                {
                    continue;
                }
                // 主配置快照尚未初始化（从未运行/编辑）时无可比较内容，跳过该用户避免误报。
                if (!ConfigSnapshotService.HasSnapshot(script.Id, user.UserId))
                {
                    continue;
                }

                if (spec.TaskProtocol?.Version == "0.1.0" && _taskProtocolAssessment is not null)
                {
                    if (assessmentBudget.IsCancellationRequested)
                    {
                        ran = true;
                        diagnostics.Add(_taskProtocolAssessment.Error(spec, user, "配置诊断达到本次保存的时间预算，已停止继续检查"));
                        break;
                    }
                    try
                    {
                        TaskPlan? plan = await _taskProtocolAssessment.RunAsync(
                            spec,
                            user,
                            "script-save",
                            assessmentBudget.Token).ConfigureAwait(false);
                        if (plan is not null)
                        {
                            ran = true;
                            diagnostics.AddRange(_taskProtocolAssessment.ToDiagnostics(plan, user));
                        }
                    }
                    catch (Exception ex)
                    {
                        ran = true;
                        diagnostics.Add(_taskProtocolAssessment.Error(spec, user, ex.Message));
                        Logger.Warn($"[任务协议配置校验] 用户「{user.UserName}」校验失败（不阻断保存）：{ex.Message}");
                        if (assessmentBudget.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                    continue;
                }

                if (spec.ConfigValidator is null)
                {
                    continue;
                }
                ConfigValidationResult result = await ConfigValidationScriptRunner.ExecuteAsync(
                    spec.ConfigValidator,
                    spec.Script,
                    user,
                    ConfigPaths.StoreDir(script.Id, user.UserId),
                    "script-save",
                    BuildExtraSnapshots(script.Id, user.UserId, spec.ExtraConfigPaths)).ConfigureAwait(false);
                if (!result.Ran)
                {
                    continue;
                }
                ran = true;
                error ??= result.Error;
                // Feedback belongs to a user binding. Keep de-duplication within
                // that binding only; identical wording from another user is a
                // separate configuration fact and must remain visible.
                string bindingKey = user.UserId + ":" + script.Id;
                foreach (ConfigValidationToast toast in result.Toasts)
                {
                    if (toastKeys.Add(bindingKey + "|" + toast.Kind + "|" + toast.Message))
                    {
                        toasts.Add(toast);
                    }
                }
                foreach (ConfigValidationNotification notification in result.Notifications)
                {
                    if (notificationKeys.Add(bindingKey + "|" + notification.Kind + "|" + notification.Title + "|" + notification.Body))
                    {
                        notifications.Add(notification);
                    }
                }
            }
            if (!ran && error is null)
            {
                return null;
            }
            return new ConfigValidationResult(true, error ?? "", Array.Empty<string>(), toasts, notifications)
            {
                Diagnostics = diagnostics,
            };
        }
        catch (Exception ex)
        {
            Logger.Warn($"[专项配置校验] 保存脚本实例校验失败（不阻断保存）：{ex.Message}");
            return null;
        }
    }

    /// <summary>附加配置路径 → 该用户 store-extra 快照的只读视图（编辑会话与保存校验共用）。</summary>
    public static IReadOnlyList<ConfigValidationExtraSnapshot> BuildExtraSnapshots(
        string scriptId,
        string userKey,
        IReadOnlyList<string> extraPaths)
    {
        if (extraPaths.Count == 0)
        {
            return Array.Empty<ConfigValidationExtraSnapshot>();
        }
        return extraPaths
            .Select(path => new ConfigValidationExtraSnapshot(path, ConfigPaths.StoreExtraDir(scriptId, userKey, path)))
            .ToList();
    }
}
