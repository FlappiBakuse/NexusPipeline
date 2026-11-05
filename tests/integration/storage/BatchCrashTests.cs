using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class BatchCrashTests(ITestOutputHelper output)
{
    [Theory(DisplayName = "Killing The Actual Batch Saving Process Preserves All Old Or All New Snapshots And An Exactly Replayable Operation")]
    [InlineData("Prepared", -1, false)]
    [InlineData("OperationAccepted", -1, false)]
    [InlineData("ItemPublished", 0, false)]
    [InlineData("ItemPublished", 1, false)]
    [InlineData("TransactionCommitted", -1, true)]
    public async Task RecoversAtomicBatches(
        string point, int ordinal, bool committed)
    {
        var fixture = new ConfigurationBatchFixture();
        ConfigurationRevisionBatch initial = new([fixture.Capture("main", "original-main"), fixture.Capture("extra", "original-extra")]);
        fixture.Save(initial);
        ConfigurationRevisionBatch next = new([fixture.Capture("main", "updated-main", 1), fixture.Capture("extra", "updated-extra", 1)]);
        var operation = ConfigurationBatchStore.Identity(Guid.NewGuid(), "fixture", next);
        var input = Path.Combine(fixture.Root, "input.json");
        var report = Path.Combine(fixture.Root, "fault-report.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { runtimeRoot = fixture.Instance.RuntimeRoot,
            diagnosticRoot = Path.Combine(fixture.Root, "writer-diagnostics"), fixtureRoot = fixture.Root, operation, batch = next, point, ordinal }));
        using var saving = new OwnedCrashProcess();
        var identity = saving.Start(["configuration-batch", input, report], fixture.Root, fixture.Instance.InstanceId,
            operation.OperationId, Path.Combine(fixture.Root, "saving-process.json"));
        var fault = await OwnedCrashProcess.ReadReportAsync(report);
        Assert.Equal("faultReady", fault.GetProperty("status").GetString());
        Assert.Equal(point, fault.GetProperty("point").GetString());
        Assert.Equal(ordinal, fault.GetProperty("resourceOrdinal").GetInt32());
        Assert.Equal(identity.ProcessId, fault.GetProperty("processId").GetInt32());
        var stopped = await saving.StopAsync();
        Assert.True(stopped.CompleteJobStopped); Assert.Equal(0u, stopped.Accounting.ActiveProcesses);
        File.WriteAllBytes(Path.Combine(fixture.Root, "stopped-processes.json"), JsonSerializer.SerializeToUtf8Bytes(stopped));
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "recovery-diagnostics"), new JournalStartupAdmission(fixture.Instance, new()));
        var store = new ConfigurationRevisionStore(fixture.Instance, database, new());
        foreach (var change in committed ? next.Changes : initial.Changes)
        {
            var current = store.Read(change.Address)!;
            Assert.Equal(committed ? 2 : 1, current.Revision);
            Assert.Equal(change.Tree.Entries, current.Tree.Entries);
        }
        var batches = new ConfigurationBatchStore(fixture.Instance, database, new());
        Assert.Equal(committed, batches.Lookup(operation, next) is not null);
        using (var prepared = batches.Prepare(next)) Assert.Equal(!committed, batches.Commit(operation, prepared, DateTimeOffset.UtcNow).IsNew);
        var replay = batches.Lookup(operation, next)!;
        Assert.False(replay.IsNew);
        Assert.All(replay.Items, item => Assert.Equal(2, item.Saved.Revision));
        Assert.All(next.Changes, change => Assert.Equal(2, store.Read(change.Address)!.Revision));
        output.WriteLine(JsonSerializer.Serialize(new { point, ordinal, committed, stopped, operationId = operation.OperationId,
            finalRevisions = replay.Items.Select(item => new { item.Saved.Address, item.Saved.Revision }),
            scope = "Actual owned process interruption at immutable preparation and SQL transaction publication boundaries; not physical power loss or external editor restoration" }));
    }
}
