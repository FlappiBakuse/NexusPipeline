using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Records;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Host.Control.Automation;

[SupportedOSPlatform("windows")]
internal sealed class AccountSummaries(AutomationDefinitionService definitions, RecordSummaryReader records,
    ExecutionRuntimeConsumer execution, TimeProvider clock)
{
    internal object Read(AuthenticatedControlRequest request)
    {
        Require(request);
        var catalog = definitions.Read();
        var now = clock.GetUtcNow();
        var users = catalog.Users.ToDictionary(user => user.Id);
        var scripts = catalog.Scripts.Where(script => script.Kind == AutomationKind.Specialized).Select(script => script.Id).ToHashSet();
        var bindings = catalog.Bindings.Select(binding => AutomationBindingPolicy.Resolve(users[binding.UserId], binding))
            .Where(binding => binding.IsEnabled && scripts.Contains(binding.ScriptId)).ToArray();
        var latest = records.ReadBindings(bindings).ToDictionary(item => item.BindingId);
        var next = ScheduleMinutePolicy.NextForUsers(catalog, now).ToDictionary(item => item.UserId);
        var active = execution.ReadActive(request.SubjectId).SelectMany(run => run.Steps.Where(step => step.HasTaskReport && step.UserId is not null &&
                step.BindingId is not null && step.Phase is "preparing" or "running" or "collecting" or "restoring")
                .Select(step => (run.RunId, step.BindingId, UserId: Guid.Parse(step.UserId!))))
            .Distinct().GroupBy(item => item.UserId).ToDictionary(group => group.Key, group => group.Count());
        var byUser = bindings.GroupBy(binding => binding.UserId).ToDictionary(group => group.Key, group => group.ToArray());
        var items = catalog.Users.Select(user =>
        {
            var reports = byUser.GetValueOrDefault(user.Id, []).Select(binding => latest.GetValueOrDefault(binding.Id) ??
                new BindingTaskSummary(binding.Id, user.Id, binding.ScriptId, null, null, "muted", "not_run")).ToArray();
            var worst = reports.OrderBy(item => Rank(item.Tone)).ThenByDescending(item => item.EndedAt)
                .ThenBy(item => item.RunId?.ToString("D"), StringComparer.Ordinal).FirstOrDefault();
            next.TryGetValue(user.Id, out var upcoming);
            return new { userId = user.Id, summaryReason = worst?.Reason ?? "no_bindings", summaryTone = worst?.Tone ?? "muted",
                runId = worst?.RunId, activeCount = active.GetValueOrDefault(user.Id), nextRunAt = upcoming?.NextRunAt,
                queueId = upcoming?.QueueId, bindings = reports };
        }).ToArray();
        Require(request);
        return new { catalogRevision = catalog.Revision, progressRevision = execution.ProgressRevision, observedAt = now, items };
    }
    private static int Rank(string tone) => tone switch { "bad" => 0, "warn" => 1, "muted" => 2, _ => 3 };
    private static void Require(AuthenticatedControlRequest request)
    {
        request.Require(ControlCapability.AutomationRead); request.Require(ControlCapability.StatusRead);
        request.Require(ControlCapability.RecordsRead); request.Require(ControlCapability.SchedulingRead);
    }
}
