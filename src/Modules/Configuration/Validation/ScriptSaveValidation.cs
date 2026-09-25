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
/// 保存脚本实例后的专项配置诊断（script-save 语境）：对每个绑定用户运行 taskProtocol 诊断，
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

            var diagnostics = new List<ConfigValidationDiagnostic>();
            using var assessmentBudget = new CancellationTokenSource(TaskProtocolAssessmentBudget);
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

            }
            if (!ran)
            {
                return null;
            }
            return new ConfigValidationResult(true, "", Array.Empty<string>(), Array.Empty<ConfigValidationToast>(), Array.Empty<ConfigValidationNotification>())
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

}
