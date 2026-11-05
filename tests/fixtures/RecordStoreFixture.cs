using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

internal static class RecordStoreFixture
{
    internal static (StateDatabase Database, RunRecordStore Store) NewStore()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var instance = new InstanceRoot(new ContractReader()).OpenOrCreate(root);
        var database = StateDatabase.CreateFresh(instance);
        return (database, new(database));
    }

    internal static CompletedRunRecord Record(int number, Guid automation)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(number + 1).CopyTo(bytes, 0);
        bytes[15] = 1;
        var run = new Guid(bytes);
        bytes[15] = 2;
        return new(automation, null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(number / 7), number,
            new(run, new Guid(bytes), 1, EngineOutcome.Succeeded, BusinessOutcome.Succeeded, LifecycleOutcome.Completed, RecoveryOutcome.NotRequired, "fixture_completed"));
    }
}
