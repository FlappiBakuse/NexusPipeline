using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.StorageTests;

internal static class AttemptPlanSeed
{
    internal static RunAttemptPlan Create(ExecutionPlanFixture fixture, PreparedExecutionPlan total)
    {
        var runId = fixture.Plan.RunId;
        var attemptId = Guid.NewGuid().ToString("D");
        return new()
        {
            Format = "nexus.execution.run-attempt-plan", Version = 1, InstanceId = fixture.Instance.InstanceId.ToString("D"),
            OperationId = Guid.NewGuid().ToString("D"), JournalId = Guid.NewGuid().ToString("D"), RunId = runId,
            AttemptId = attemptId, AttemptOrdinal = 1, ConfigurationPlans = [], InitialLogs = [],
            ExecutionPlan = new() { RelativePath = total.Reference.RelativePath, SizeBytes = total.Reference.SizeBytes, Sha256 = total.Reference.Sha256 },
            OutputDirectory = $"runs/{runId}/{attemptId}", LogDirectory = $"logs/{runId}/{attemptId}", ResourceKeys = fixture.Plan.ResourceKeys
        };
    }
}
