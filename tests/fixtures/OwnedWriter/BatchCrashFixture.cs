using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class BatchCrashFixture
{
    internal static int Run(string[] arguments)
    {
        if (arguments is not [var inputPath, var reportPath]) return 2;
        using var input = JsonDocument.Parse(File.ReadAllBytes(inputPath));
        var root = input.RootElement;
        var instance = new InstanceRoot(new()).OpenExisting(root.GetProperty("runtimeRoot").GetString()!);
        using var instanceLease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, root.GetProperty("diagnosticRoot").GetString()!, new JournalStartupAdmission(instance, new()));
        var index = new StartupJournalRecovery(instance, new(), database,
            new(root.GetProperty("fixtureRoot").GetString()!, Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(instanceLease);
        var lifecycle = new HostLifecycle(index, TimeProvider.System);
        var operation = root.GetProperty("operation").Deserialize<OperationIdentity>()!;
        var batch = root.GetProperty("batch").Deserialize<ConfigurationRevisionBatch>()!;
        var point = Enum.Parse<BatchPersistencePoint>(root.GetProperty("point").GetString()!);
        var ordinal = root.GetProperty("ordinal").GetInt32();
        var service = new ConfigurationBatchService(instance, database, new(), instanceLease, new(lifecycle), new Pause(point, ordinal, reportPath));
        service.Save(operation, batch, DateTimeOffset.UtcNow);
        return 3;
    }

    private sealed class Pause(BatchPersistencePoint expected, int ordinal, string report) : IBatchPersistenceObserver
    {
        public void Reached(BatchPersistencePoint point, Guid operationId, int resourceOrdinal)
        {
            if (point != expected || resourceOrdinal != ordinal) return;
            OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", point = point.ToString(), resourceOrdinal,
                operationId, processId = Environment.ProcessId });
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
