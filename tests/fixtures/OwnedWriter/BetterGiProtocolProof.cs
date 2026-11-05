using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;

internal sealed class BetterGiProtocolProof(TaskAdapterExecutor executor, Guid runId, Guid attemptId)
{
    private readonly List<string> _checks = [];
    internal IReadOnlyList<string> Checks => _checks;

    internal void Run()
    {
        var duplicate = Configuration(true);
        var tasks = Invoke("discover", duplicate).Tasks!;
        Check("discovery_preserved_ids_and_order", tasks.Select(task => task.TaskId).SequenceEqual(["bettergi:mail-2", "bettergi:custom", "bettergi:mail-1"]));
        Check("duplicate_names_remained_distinct", tasks.Count(task => task.Title == "领取邮件") == 2);
        Check("unknown_task_safety_remained_unknown", tasks.Single(task => task.TaskId == "bettergi:custom").RetrySafety == "unknown");
        var ambiguous = Invoke("observe", duplicate, tasks, ["邮件：全部领取"]).Observations!;
        Check("duplicate_mail_success_not_inferred", ambiguous.All(item => item.State == "unknown"));
        var single = Configuration(false);
        var frozen = Invoke("discover", single).Tasks!;
        var failed = Invoke("observe", single, frozen, ["领取邮件奖励异常: fixture failure"]).Observations!;
        Check("explicit_mail_failure_observed", failed.Single(item => item.TaskId == "bettergi:mail-1").State == "failed");
        Check("unrelated_task_not_inferred", failed.Single(item => item.TaskId == "bettergi:custom").State == "unknown");
        var retry = Invoke("retry", single, frozen, observations: failed);
        Check("retry_only_frozen_safe_mail", retry.SelectedTaskIds!.SequenceEqual(["bettergi:mail-1"]));
        var contradictory = Invoke("observe", single, frozen, ["邮件：全部领取", "领取邮件奖励异常: fixture failure"]).Observations!;
        Check("contradictory_mail_evidence_unknown", contradictory.Single(item => item.TaskId == "bettergi:mail-1").State == "unknown");
        Check("unknown_outcome_not_retried", Invoke("retry", single, frozen, observations: contradictory).SelectedTaskIds!.Count == 0);
        VerifyConfiguration(single);
        VerifyCursor(single);
        ExpectReject("changed_config_cannot_reuse_frozen_tasks", () => Invoke("observe", duplicate, frozen));
        ExpectReject("duplicate_json_member_rejected", () => Invoke("discover", "{\"TaskEnabledList\":{\"mail\":true,\"mail\":false}}"));
        ExpectReject("nonfinite_upstream_value_rejected", () => Invoke("discover", "{\"TaskEnabledList\":{},\"x\":1e999}"));
        VerifyRetryPolicy();
    }

    private void VerifyConfiguration(string document)
    {
        var result = Invoke("configuration", document);
        Check("completion_action_diagnosed", result.Diagnostics!.Any(item => item.Code == "bettergi_completion_action_requires_host_policy"));
        var patch = result.ConfigPatches!.Single();
        Check("repair_is_bound_proposal", patch.ExpectedRevision == 7 && patch.ResourceId == "one-dragon" &&
            patch.SourceSha256 == Digest(document) && patch.Edits.Single().Expected.GetString() == "Shutdown" && patch.Edits.Single().Value.GetString() == "None");
        Check("missing_revision_cannot_propose_patch", Invoke("configuration", document, revision: false).ConfigPatches!.Count == 0);
    }

    private void VerifyCursor(string document)
    {
        var value = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(document)!;
        value["NextTaskId"] = JsonSerializer.SerializeToElement("mail-1");
        var resumed = JsonSerializer.Serialize(value);
        var tasks = Invoke("discover", resumed).Tasks!;
        var failed = Invoke("observe", resumed, tasks, ["领取邮件奖励异常: fixture failure"]).Observations!;
        Check("native_resume_cursor_blocks_selective_retry", Invoke("retry", resumed, tasks, observations: failed).SelectedTaskIds!.Count == 0);
        value["NextTaskId"] = JsonSerializer.SerializeToElement("missing-task");
        Check("unknown_cursor_is_blocking_diagnostic", Invoke("configuration", JsonSerializer.Serialize(value)).Diagnostics!.Any(item => item.Severity == "blocking"));
    }

    private void VerifyRetryPolicy()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = new FrozenRetryPolicy(RetrySafety.Safe, true, 3, now.AddMinutes(5), TimeSpan.Zero, "fixture-idempotency");
        var evidence = new RetryEvidence(AttemptOutcome.Failed, WriterState.Stopped, true, false, true, 1, now.AddSeconds(-1));
        Check("host_policy_still_required", !RetryDecision.Evaluate(policy with { HostApproved = false }, evidence, now).IsEligible);
        Check("running_writer_blocks_retry", !RetryDecision.Evaluate(policy, evidence with { Writers = WriterState.Running }, now).IsEligible);
        Check("unrestored_configuration_blocks_retry", !RetryDecision.Evaluate(policy, evidence with { IsConfigurationRestored = false }, now).IsEligible);
        Check("unknown_safety_never_upgraded", !RetryDecision.Evaluate(policy with { Safety = RetrySafety.Unknown }, evidence, now).IsEligible);
        Check("safe_policy_with_verified_prerequisites_eligible", RetryDecision.Evaluate(policy, evidence, now).IsEligible);
    }

    private TaskAdapter Invoke(string phase, string document, IReadOnlyList<TaskDefinitionsTask>? tasks = null, string[]? logs = null,
        IReadOnlyList<TaskDefinitionsObservation>? observations = null, bool revision = true)
    {
        var operation = Guid.NewGuid();
        var digest = Digest(document);
        var request = new TaskAdapter
        {
            Format = "nexus.task.adapter", Version = 1, Direction = "request", Phase = phase, ExtensionId = "bettergi",
            OperationId = operation.ToString("D"), RunId = phase is "observe" or "retry" ? runId.ToString("D") : null,
            AttemptId = phase is "observe" or "retry" ? attemptId.ToString("D") : null,
            PlanDigest = phase is "observe" or "retry" ? digest : null,
            Resources = [new() { ResourceId = "one-dragon", Sha256 = digest, Content = document, Revision = revision ? 7 : null }],
            Tasks = tasks, LogLines = logs, Observations = observations, IdempotencyKey = phase == "retry" ? "fixture-idempotency" : null
        };
        return executor.Execute(JsonSerializer.SerializeToUtf8Bytes(request), new("bettergi", operation, phase, phase is "observe" or "retry" ? runId : null,
            phase is "observe" or "retry" ? attemptId : null, phase is "observe" or "retry" ? digest : null), default);
    }

    private void ExpectReject(string name, Action action)
    {
        try { action(); }
        catch (Exception error) when (error is JsonException || error.GetType().Namespace?.StartsWith("Jint", StringComparison.Ordinal) == true)
        { Check(name, true); return; }
        throw new IOException("BetterGI fixture accepted invalid input: " + name);
    }

    private void Check(string name, bool valid)
    {
        if (!valid || _checks.Contains(name, StringComparer.Ordinal)) throw new IOException("BetterGI proof failed: " + name);
        _checks.Add(name);
    }

    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Configuration(bool duplicateEnabled) => JsonSerializer.Serialize(new
    {
        TaskEnabledList = new Dictionary<string, bool> { ["mail-1"] = true, ["mail-2"] = duplicateEnabled, ["custom"] = true },
        TaskDefinitions = new Dictionary<string, string> { ["mail-1"] = "领取邮件", ["mail-2"] = "领取邮件", ["custom"] = "未登记任务" },
        TaskOrder = new[] { "mail-2", "custom", "mail-1" }, NextTaskId = "", CompletionAction = "Shutdown"
    });
}
