using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Configuration.Validation;

/// <summary>Bounded session-local save feedback; preview and history never enter this service.</summary>
internal sealed class ConfigDiagnosticFeedback
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, string>> _bindings = new(StringComparer.Ordinal);
    private const int MaximumBindings = 1024;

    internal IReadOnlyList<(TaskConfigCheck Check, bool ShouldNotify)> Select(TaskPlan plan, string bindingKey)
    {
        if (plan.ConfigAssessment is not { } assessment) return [];
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<(TaskConfigCheck, bool)>();
        lock (_gate)
        {
            _bindings.TryGetValue(bindingKey, out var previous);
            foreach (TaskConfigCheck check in assessment.Checks)
            {
                if (check.Evaluation is "satisfied" or "not_applicable") continue;
                string key = check.RuleId + "\n" + check.Scope.ToJsonString();
                string identity = TaskProtocolJson.Write(new { plan.PluginId, plan.PluginVersion,
                    plan.CurrentReadiness?.ConfigRevision, plan.CurrentReadiness?.ContextFingerprint,
                    plan.DisplaySnapshot?.LocalizationHash, Check = check });
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                next[key] = hash;
                result.Add((check, previous is null || !previous.TryGetValue(key, out string? old) || old != hash));
            }
            // A successful recheck clears recovered findings, so recurrence is observable even if bytes revert.
            _bindings.Remove(bindingKey);
            if (next.Count > 0)
            {
                if (_bindings.Count >= MaximumBindings) _bindings.Remove(_bindings.Keys.First());
                _bindings.Add(bindingKey, next);
            }
        }
        return result;
    }
}
