using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionInitialJournal
{
    internal static ExecutionJournal Create(Guid instanceId, RegisteredJournal journal, string runId, string attemptId) => new()
    {
        Format = "nexus.execution.journal", Version = 1, InstanceId = instanceId.ToString("D"),
        JournalId = journal.JournalId.ToString("D"), OperationId = journal.OperationId.ToString("D"), OperationKind = "runAttempt",
        RunId = runId, AttemptId = attemptId, Phase = "prepared", Revision = 1,
        PlanPath = journal.PlanPath, PlanDigest = journal.PlanDigest, UpdatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"),
        ResourceKeys = journal.ResourceKeys, Participants = [], Receipts = []
    };
}
