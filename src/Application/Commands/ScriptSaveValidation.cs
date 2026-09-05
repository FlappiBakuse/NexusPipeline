using NexusPipeline.App.Abstractions;
using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Configuration;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.App.Commands;

/// <summary>
/// 保存脚本实例后的专项配置校验（script-save 语境）：对每个绑定用户以其 store 为根运行插件的
/// configValidator（只读比较 + 通知），聚合去重通知供 Web 响应返回。保存结果不受校验影响；
/// 通用脚本、插件无校验器、解析失败或无绑定用户时返回 null。
/// </summary>
internal static class ScriptSaveValidation
{
    public static async Task<ConfigValidationResult?> RunForScriptAsync(ScriptInstance script)
    {
        try
        {
            RuntimeContext ctx = RuntimeContext.Instance;
            if (string.IsNullOrWhiteSpace(script.PluginType))
            {
                return null;
            }
            ResolvedScriptSpec sharedSpec = ctx.Resolve<ScriptSpecResolver>().Resolve(script);
            if (!sharedSpec.Succeeded || sharedSpec.ConfigValidator is null)
            {
                return null;
            }
            List<ResolvedScriptUser> targets = ctx.EntityState.SnapshotUsers()
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
            HashSet<string> toastKeys = new(StringComparer.Ordinal);
            HashSet<string> notificationKeys = new(StringComparer.Ordinal);
            string? error = null;
            bool ran = false;
            ScriptSpecResolver resolver = ctx.Resolve<ScriptSpecResolver>();
            foreach (ResolvedScriptUser user in targets)
            {
                // 按用户绑定输入实例化校验语境：接管配置是用户级选择
                ResolvedScriptSpec spec = user.Binding.ConfigInputs.Count == 0
                    ? sharedSpec
                    : resolver.Resolve(script, user.Binding.ConfigInputs);
                if (!spec.Succeeded || spec.ConfigValidator is null)
                {
                    continue;
                }
                // 主配置快照尚未初始化（从未运行/编辑）时无可比较内容，跳过该用户避免误报。
                if (!UserConfigManager.HasSnapshot(script.Id, user.UserId))
                {
                    continue;
                }
                ConfigValidationResult result = await ConfigValidationScriptRunner.ExecuteAsync(
                    spec.ConfigValidator,
                    spec.Script,
                    user,
                    UserConfigManager.StoreDir(script.Id, user.UserId),
                    "script-save",
                    BuildExtraSnapshots(script.Id, user.UserId, spec.ExtraConfigPaths)).ConfigureAwait(false);
                if (!result.Ran)
                {
                    continue;
                }
                ran = true;
                error ??= result.Error;
                foreach (ConfigValidationToast toast in result.Toasts)
                {
                    if (toastKeys.Add(toast.Kind + "|" + toast.Message))
                    {
                        toasts.Add(toast);
                    }
                }
                foreach (ConfigValidationNotification notification in result.Notifications)
                {
                    if (notificationKeys.Add(notification.Kind + "|" + notification.Title + "|" + notification.Body))
                    {
                        notifications.Add(notification);
                    }
                }
            }
            if (!ran && error is null)
            {
                return null;
            }
            return new ConfigValidationResult(true, error ?? "", Array.Empty<string>(), toasts, notifications);
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
            .Select(path => new ConfigValidationExtraSnapshot(path, ConfigSwapPaths.StoreExtraDir(scriptId, userKey, path)))
            .ToList();
    }
}
