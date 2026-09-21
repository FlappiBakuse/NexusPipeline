using System.Text.Json.Nodes;
using NexusPipeline.Shared.Localization;

namespace NexusPipeline.Modules.Notifications;

internal static class TaskNotificationFormatter
{
    internal static IReadOnlyList<string> Format(JsonObject report)
    {
        var tasks = (report["originalPlan"]?["tasks"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(t => t["enabled"]?.GetValue<bool>() == true && t["role"]?.GetValue<string>() == "business")
            .ToDictionary(t => t["id"]!.GetValue<string>(), StringComparer.Ordinal);
        var results = (report["finalTaskResults"] as JsonArray ?? []).OfType<JsonObject>()
            .ToDictionary(t => t["taskId"]!.GetValue<string>(), StringComparer.Ordinal);
        string Text(string key, string fallback) => HostLocalization.TranslateNamed("notification.tasks." + key, fallback);
        string Name(JsonObject task)
        {
            var name = task["name"]?.GetValue<string>() ?? task["id"]!.GetValue<string>();
            if (task["parentId"]?.GetValue<string>() is { } parent && tasks.TryGetValue(parent, out var owner))
                name = (owner["name"]?.GetValue<string>() ?? parent) + "／" + name;
            return name.Replace('\r', ' ').Replace('\n', ' ');
        }
        var lines = new List<string>();
        foreach (var (status, key, label, mandatory) in new[] {
            ("failed", "failed", "运行失败任务", true), ("partial", "partial", "部分失败任务", false),
            ("unknown", "unknown", "无法判定任务", false), ("blocked", "blocked", "未执行任务", false),
            ("cancelled", "cancelled", "已取消任务", false), ("succeeded", "succeeded", "运行成功任务", true),
            ("skipped", "skipped", "正常跳过任务", false) })
        {
            var names = tasks.Where(p => (results.GetValueOrDefault(p.Key)?["status"]?.GetValue<string>() ?? "unknown") == status)
                .Where(p => p.Value["parentId"] is null || status is "failed" or "unknown" or "blocked")
                .Select(p => Name(p.Value)).ToArray();
            if (!mandatory && names.Length == 0) continue;
            // Keep each category, especially failures and unknowns, even when a channel limits message size.
            var visible = names.Take(12).Select(n => n.Length > 120 ? n[..117] + "…" : n).ToList();
            string value = visible.Count == 0 ? Text("none", "无") : string.Join("、", visible);
            if (names.Length > visible.Count) value += HostLocalization.TranslateNamed("notification.tasks.more", "（另 {count} 项，详见历史记录）",
                new Dictionary<string, object?> { ["count"] = names.Length - visible.Count });
            lines.Add(Text(key, label) + "：" + value);
        }
        if (report["lifecycleOutcome"]?.GetValue<string>() is "failed" or "interrupted") lines.Add(Text("lifecycle", "实例运行异常"));
        if (report["summary"]?["recovered"]?.GetValue<bool>() == true) lines.Add(Text("recovered", "重试恢复情况：已恢复成功"));
        if ((report["diagnostics"] as JsonArray ?? []).Any(d => d?["code"]?.GetValue<string>() == "recovery_conflict"))
            lines.Add(Text("recovery", "配置恢复告警：保留现场，未同步临时选择"));
        return lines;
    }
}
