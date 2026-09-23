using System.Text.Json.Nodes;
using NexusPipeline.Shared.Localization;
using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Notifications;

internal static class TaskNotificationFormatter
{
    internal static IReadOnlyList<string> Format(JsonObject report, string? historyId = null)
    {
        TaskDisplaySnapshot? display = null;
        if ((report["displaySnapshot"] ?? report["originalPlan"]?["displaySnapshot"]) is JsonObject frozen)
            try { display = TaskProtocolJson.Read<TaskDisplaySnapshot>(frozen.ToJsonString()); }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { }
        var allTasks = (report["originalPlan"]?["tasks"] as JsonArray ?? []).OfType<JsonObject>()
            .ToDictionary(t => t["id"]!.GetValue<string>(), StringComparer.Ordinal);
        var tasks = allTasks.Where(p => p.Value["enabled"]?.GetValue<bool>() == true
            && p.Value["role"]?.GetValue<string>() == "business").ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var results = (report["finalTaskResults"] as JsonArray ?? []).OfType<JsonObject>()
            .ToDictionary(t => t["taskId"]!.GetValue<string>(), StringComparer.Ordinal);
        string Text(string key, string fallback) => HostLocalization.TranslateNamed("notification.tasks." + key, fallback);
        string Status(string id)
        {
            string? status = results.GetValueOrDefault(id)?["status"]?.GetValue<string>();
            return status is "failed" or "partial" or "blocked" or "cancelled" or "succeeded" or "skipped" or "unknown" ? status : "unknown";
        }
        bool IsDescendant(string id, string ancestor)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (allTasks.TryGetValue(id, out var task) && task["parentId"]?.GetValue<string>() is { } parent && seen.Add(parent))
            { if (parent == ancestor) return true; id = parent; }
            return false;
        }
        string Name(JsonObject task)
        {
            string Label(JsonObject item) => TaskDisplaySnapshot.Resolve(item["nameText"] as JsonObject, display, LocaleContext.Current,
                item["name"]?.GetValue<string>() ?? item["id"]!.GetValue<string>());
            var names = new List<string> { Compact(Label(task), 36) };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (task["parentId"]?.GetValue<string>() is { } parent && seen.Add(parent) && allTasks.TryGetValue(parent, out var owner))
            { names.Add(Compact(Label(owner), 36)); task = owner; }
            bool shortened = names.Count > 3;
            return (shortened ? "…／" : "") + string.Join("／", names.Take(3).Reverse());
        }
        string Reason(JsonObject item) => TaskDisplaySnapshot.Resolve(item["reasonText"] as JsonObject, display,
            LocaleContext.Current, item["reasonCode"]?.GetValue<string>() ?? "");
        var lines = new List<string>();
        void Category(string key, string fallback, IEnumerable<string> values, bool mandatory = false)
        {
            var entries = values.Select(value => Compact(value, 120)).ToArray();
            if (!mandatory && entries.Length == 0) return;
            var visible = new List<string>();
            int length = 0;
            foreach (var entry in entries)
            {
                if (visible.Count == 12 || length + entry.Length + visible.Count > 360) break;
                visible.Add(entry); length += entry.Length;
            }
            string value = entries.Length == 0 ? Text("none", "无") : string.Join("、", visible);
            if (entries.Length > visible.Count) value += HostLocalization.TranslateNamed("notification.tasks.more", "（另 {count} 项，详见历史记录）",
                new Dictionary<string, object?> { ["count"] = entries.Length - visible.Count });
            lines.Add(Text(key, fallback) + "：" + value);
        }
        foreach (var (status, key, label, mandatory) in new[] {
            ("succeeded", "succeeded", "运行成功任务", true), ("failed", "failed", "运行失败任务", true),
            ("partial", "partial", "部分失败任务", false),
            ("unknown", "unknown", "无法判定任务", false), ("blocked", "blocked", "未执行任务", false),
            ("cancelled", "cancelled", "已取消任务", false),
            ("skipped", "skipped", "正常跳过任务", false) })
        {
            var names = tasks.Where(p => Status(p.Key) == status)
                .Where(p => p.Value["parentId"] is null || status is not ("succeeded" or "skipped"))
                // Show the affected leaf with its parent path, rather than counting both as separate failures.
                .Where(p => !tasks.Any(child => IsDescendant(child.Key, p.Key) && Status(child.Key) is not ("succeeded" or "skipped")
                    && (status == "failed" ? Status(child.Key) == "failed"
                        : status == "partial" ? Status(child.Key) is "failed" or "partial" : true)))
                .Select(p => Name(p.Value) + (status is not ("succeeded" or "skipped") && results.GetValueOrDefault(p.Key) is { } item
                    && Reason(item) is { Length: > 0 } reason ? "（" + Compact(reason, 72) + "）" : ""));
            Category(key, label, names, mandatory);
        }
        var incidents = (report["incidents"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(e => e["incident"] is JsonObject)
            .GroupBy(e => (e["attemptId"]?.GetValue<string>(), e["incident"]!["id"]?.GetValue<string>()))
            .Select(group => group.Last()["incident"]!.AsObject()).ToArray();
        string Incident(JsonObject item) => (item["taskId"]?.GetValue<string>() is { } id && allTasks.TryGetValue(id, out var task)
            ? Name(task) : Text("unattributed", "未归属异常")) + "：" + Compact(Reason(item), 72);
        Category("incidents", "未恢复异常", incidents.Where(i => i["resolution"]?.GetValue<string>() != "recovered").Select(Incident));
        Category("incident_recovered", "已恢复异常", incidents.Where(i => i["resolution"]?.GetValue<string>() == "recovered").Select(Incident));
        if (report["summary"]?["counts"] is JsonObject counts)
            lines.Add(HostLocalization.TranslateNamed("notification.tasks.progress", "业务完成：{completed}/{total}",
                new Dictionary<string, object?> { ["completed"] = (counts["succeeded"]?.GetValue<int>() ?? 0) + (counts["skipped"]?.GetValue<int>() ?? 0),
                    ["total"] = counts["total"]?.GetValue<int>() ?? 0 }));
        if (report["lifecycleOutcome"]?.GetValue<string>() is "failed" or "interrupted") lines.Add(Text("lifecycle", "实例运行异常"));
        if (report["summary"]?["recovered"]?.GetValue<bool>() == true) lines.Add(Text("recovered", "重试恢复情况：已恢复成功"));
        if ((report["diagnostics"] as JsonArray ?? []).Any(d => d?["code"]?.GetValue<string>() == "recovery_conflict"))
            lines.Add(Text("recovery", "配置恢复告警：保留现场，未同步临时选择"));
        if ((historyId ?? report["runId"]?.GetValue<string>()) is { Length: > 0 } recordId)
            lines.Add(HostLocalization.TranslateNamed("notification.tasks.history", "历史记录：{id}",
                new Dictionary<string, object?> { ["id"] = Compact(recordId, 160) }));
        return lines;
    }

    private static string Compact(string text, int limit)
    {
        string safe = new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        if (safe.Length <= limit) return safe;
        int end = limit - 1;
        if (char.IsHighSurrogate(safe[end - 1])) end--;
        return safe[..end] + "…";
    }
}
