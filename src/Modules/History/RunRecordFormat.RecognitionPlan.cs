using System.Text.Json.Nodes;

namespace NexusPipeline.Modules.History;

internal static partial class RunRecordFormat
{
    // History validates its frozen data without requiring a currently installed plugin or its live discovery policy.
    private static void ValidateRecognitionPlan(JsonObject plan)
    {
        Fields(plan, ["protocolVersion", "planId", "origin", "pluginId", "pluginVersion", "generatedAt", "signature", "coverage",
            "tasks", "diagnostics", "behaviorSignature", "semanticsVersion", "displaySnapshot", "selectionFields", "configAssessment", "currentReadiness", "verification"]);
        Require(System.Text.Encoding.UTF8.GetByteCount(plan.ToJsonString()) <= 1024 * 1024);
        Require(plan["tasks"] is JsonArray { Count: <= 1024 } && plan["selectionFields"] is JsonArray { Count: <= 2048 }
            && plan["diagnostics"] is JsonArray { Count: <= 1024 });
        var tasks = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in plan["tasks"]!.AsArray())
        {
            Require(node is JsonObject);
            var task = node!.AsObject();
            Fields(task, ["id", "sourceKey", "name", "nameText", "parentId", "role", "enabled", "order", "countsAsUnit", "requiredForParent",
                "retryUnitId", "retryRisk", "dependencies", "detection", "configRef", "completionPolicy", "workflowRole", "observationContract",
                "retryPolicy", "recognition", "lifecycleRole", "selectionPolicy"]);
            foreach (string field in new[] { "id", "sourceKey", "name", "retryUnitId" }) Text(task[field]);
            Require(tasks.TryAdd(task["id"]!.GetValue<string>(), task));
            Require(task["role"]?.GetValue<string>() is "business" or "technical" or "cleanup"
                && task["retryRisk"]?.GetValue<string>() is "safe" or "conditional" or "unsafe" or "unknown"
                && task["detection"]?.GetValue<string>() is "supported" or "limited" or "unsupported"
                && task["recognition"]?.GetValue<string>() is "known" or "opaque"
                && task["lifecycleRole"]?.GetValue<string>() is "prepare" or "business" or "finalize"
                && task["selectionPolicy"]?.GetValue<string>() is "managed" or "preserve"
                && task["order"] is JsonValue order && order.TryGetValue<int>(out int ordinal) && ordinal >= 0);
            foreach (string field in new[] { "enabled", "countsAsUnit", "requiredForParent" })
                Require(task[field] is JsonValue flag && flag.TryGetValue<bool>(out _));
            Require(task["dependencies"] is JsonArray { Count: <= 128 });
            Strings(task["dependencies"]);
            if (task["parentId"] is { } parent) Text(parent);
            if (task["configRef"] is { } config) Text(config);
            bool counts = task["countsAsUnit"]!.GetValue<bool>();
            Require(!counts || task["parentId"] is null && task["recognition"]!.GetValue<string>() == "known"
                && task["lifecycleRole"]!.GetValue<string>() == "business");
            if (task["recognition"]!.GetValue<string>() == "opaque")
                Require(task["selectionPolicy"]!.GetValue<string>() == "preserve" && !counts && !task["requiredForParent"]!.GetValue<bool>()
                    && task["detection"]!.GetValue<string>() == "unsupported" && task["configRef"] is null
                    && task["observationContract"] is null && task["retryPolicy"] is null && task["completionPolicy"] is null && task["workflowRole"] is null);
            else ValidateKnownTask(task);
        }
        foreach (var task in tasks.Values)
        {
            Require(tasks.ContainsKey(task["retryUnitId"]!.GetValue<string>())
                && (task["parentId"] is null || tasks.ContainsKey(task["parentId"]!.GetValue<string>()))
                && Strings(task["dependencies"]).All(tasks.ContainsKey));
        }
        foreach (bool parents in new[] { false, true })
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in tasks.Keys) Visit(id);
            void Visit(string id)
            {
                if (visited.Contains(id)) return;
                Require(active.Add(id));
                if (parents) { if (tasks[id]["parentId"] is { } parent) Visit(parent.GetValue<string>()); }
                else foreach (string dependency in Strings(tasks[id]["dependencies"])) Visit(dependency);
                active.Remove(id); visited.Add(id);
            }
        }
        Require(plan["verification"] is JsonObject verification);
        var scopes = plan["verification"]!.AsObject();
        ExactFields(scopes, ["opaqueScopes"]);
        Require(scopes["opaqueScopes"] is JsonArray { Count: <= 1024 });
        var identities = tasks.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var scope in scopes["opaqueScopes"]!.AsArray())
        {
            Require(scope is JsonObject);
            ExactFields(scope!.AsObject(), ["id", "resourceId", "enabled", "reasonCode"]);
            foreach (string field in new[] { "id", "resourceId", "reasonCode" }) Text(scope[field]);
            Require(identities.Add(scope["id"]!.GetValue<string>()) && scope["enabled"] is JsonValue flag && flag.TryGetValue<bool>(out _));
        }
        bool known = tasks.Values.Any(task => task["recognition"]!.GetValue<string>() == "known");
        bool opaque = tasks.Values.Any(task => task["recognition"]!.GetValue<string>() == "opaque") || scopes["opaqueScopes"]!.AsArray().Count > 0;
        Require(plan["coverage"]?.GetValue<string>() == (opaque ? known ? "partial" : "opaque" : "complete"));
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in plan["selectionFields"]!.AsArray())
        {
            Require(node is JsonObject);
            var field = node!.AsObject();
            Fields(field, ["resourceId", "selector", "purpose", "taskIds", "taskIndices"]);
            Text(field["resourceId"]);
            Require(field["purpose"]?.GetValue<string>() is "selection" or "cursor" && field["selector"] is JsonArray { Count: > 0 and <= 32 });
            foreach (var token in field["selector"]!.AsArray())
            {
                if (token is JsonValue text && text.TryGetValue<string>(out _)) { Text(token); continue; }
                Require(token is JsonObject);
                if (token!["by"] is { } by) { ExactFields(token.AsObject(), ["by", "value"]); Text(by); Require(token["value"] is JsonValue); }
                else
                {
                    ExactFields(token.AsObject(), ["index", "guardKey", "guardValue"]);
                    Require(token["index"] is JsonValue index && index.TryGetValue<int>(out int number) && number >= 0 && token["guardValue"] is not null);
                    Text(token["guardKey"]);
                }
            }
            Require(fields.Add(field["resourceId"]!.GetValue<string>() + "\n" + field["selector"]!.ToJsonString()));
            string[] owners = Strings(field["taskIds"]);
            Require(owners.Length is > 0 and <= 1024 && owners.All(id => tasks.TryGetValue(id, out var task)
                && task["recognition"]!.GetValue<string>() == "known" && task["selectionPolicy"]!.GetValue<string>() == "managed"));
            if (field["taskIndices"] is { } indices)
            {
                Require(indices is JsonObject && field["purpose"]!.GetValue<string>() == "selection" && indices.AsObject().Count == owners.Length);
                var positions = new HashSet<int>();
                foreach (string owner in owners)
                    Require(indices[owner] is JsonValue index && index.TryGetValue<int>(out int position) && position is >= 0 and < 1024 && positions.Add(position));
            }
        }
    }

    private static void ValidateKnownTask(JsonObject task)
    {
        Require(task["lifecycleRole"]!.GetValue<string>() != "business" || task["role"]!.GetValue<string>() == "business");
        Require(task["completionPolicy"]?.GetValue<string>() is "flow" or "authoritative"
            && task["workflowRole"]?.GetValue<string>() is "daily" or "technical" or "manual_only"
            && (!task["enabled"]!.GetValue<bool>() || task["detection"]!.GetValue<string>() == "supported")
            && (task["workflowRole"]!.GetValue<string>() != "manual_only" || !task["enabled"]!.GetValue<bool>() && !task["countsAsUnit"]!.GetValue<bool>())
            && (task["workflowRole"]!.GetValue<string>() != "daily" || task["role"]!.GetValue<string>() == "business"));
        Require(task["observationContract"] is JsonObject && task["retryPolicy"] is JsonObject);
        var contract = task["observationContract"]!.AsObject();
        ExactFields(contract, ["ruleSetId", "sources", "rules"]); Text(contract["ruleSetId"]);
        Require(Strings(contract["sources"]).Length is > 0 and <= 8 && contract["rules"] is JsonArray { Count: > 0 and <= 64 });
        var rules = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in contract["rules"]!.AsArray())
        {
            Require(rule is JsonObject); ExactFields(rule!.AsObject(), ["id", "kind"]); Text(rule["id"]);
            string? kind = rule["kind"]?.GetValue<string>();
            Require(rules.Add(rule["id"]!.GetValue<string>()) && kind is "scope_started" or "flow_ended" or "business_succeeded"
                or "business_failed" or "upstream_cancelled" or "normal_skip" or "not_executed");
            Require(kind != "business_succeeded" || task["completionPolicy"]!.GetValue<string>() == "authoritative");
            Require(kind != "flow_ended" || task["completionPolicy"]!.GetValue<string>() == "flow");
        }
        var retry = task["retryPolicy"]!.AsObject(); ExactFields(retry, ["mode", "resourceConsumption", "limitRefs"]);
        Require(retry["mode"]?.GetValue<string>() is "selective_config" or "native_resume"
            && retry["resourceConsumption"] is JsonValue flag && flag.TryGetValue<bool>(out _)
            && Strings(retry["limitRefs"]).Length <= 32);
    }

    private static void Text(JsonNode? node) => Require(node is JsonValue text && text.TryGetValue<string>(out string? value) && value is { Length: > 0 and <= 2048 });
    private static void Fields(JsonObject value, string[] fields) => Require(value.All(pair => fields.Contains(pair.Key, StringComparer.Ordinal)));
}
