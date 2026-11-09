using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AssetCrashTests(ITestOutputHelper output)
{
    [Theory(DisplayName = "Killing The Actual Asset Creator Leaves Product Startup To Verify Its Writer And Recover Only Proven File Facts")]
    [InlineData(AssetPreparationPoint.Accepted, JournalPhase.Committed, false)]
    [InlineData(AssetPreparationPoint.ContentFlushed, JournalPhase.Quarantined, false)]
    [InlineData(AssetPreparationPoint.CompletedReceipt, JournalPhase.Committed, true)]
    public async Task RecoversProvenAssets(
        AssetPreparationPoint interruption, JournalPhase expectedPhase, bool published)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "asset-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var reportPath = Path.Combine(root, "fault.json");
        using var owner = new OwnedCrashProcess();
        var identity = owner.Start(["asset-preparation", root, interruption.ToString(), reportPath], root,
            Guid.NewGuid(), Guid.NewGuid(), Path.Combine(root, "process.json"));
        var report = await OwnedCrashProcess.ReadReportAsync(reportPath);
        Assert.Equal("faultReady", report.GetProperty("status").GetString());
        Assert.Equal(identity.ProcessId, report.GetProperty("processId").GetInt32());
        var instance = report.GetProperty("Instance").Deserialize<RuntimeInstance>()!;
        Assert.Equal(Path.Combine(root, "runtime"), instance.RuntimeRoot);
        var database = StateDatabase.OpenExisting(instance, Path.Combine(root, "before-stop"));
        var journalId = report.GetProperty("journalId").GetGuid();
        Assert.Equal(new WriterStopObservation(true, WriterState.Running), new JournalWriterStore(database).Observe(journalId));
        var reference = report.GetProperty("plan").Deserialize<BlobReference>()!;
        var plan = new AssetPlanCodec(instance, new(), new(instance, new())).Read(reference).ReadWire();
        var target = Path.Combine(instance.RuntimeRoot, Assert.Single(plan.Assets).Content.RelativePath);
        var before = File.Exists(target) ? PhysicalObjectIdentity.Read(target) : null;
        var stopped = await owner.StopAsync();
        Assert.True(stopped.CompleteJobStopped);
        var stoppedExitCode = await owner.AwaitExitAsync();
        Assert.NotEqual(0, stoppedExitCode);
        Assert.Equal(new WriterStopObservation(true, WriterState.Stopped), new JournalWriterStore(database).Observe(journalId));
        using var lease = InstanceLease.Acquire(instance, new());
        var roots = new PayloadApplyRoots(Path.Combine(root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"));
        var reopened = StateDatabase.OpenExisting(instance, Path.Combine(root, "startup-inspection"), new JournalStartupAdmission(instance, new(), roots));
        var index = new StartupJournalRecovery(instance, new(), reopened, roots).Recover(lease);
        var final = Assert.Single(new RecoveryJournalRegistry(reopened).ReadSnapshot().Journals);
        Assert.Equal(expectedPhase, final.Phase);
        Assert.Equal(published ? 1 : 0, new AutomationDefinitionStore(reopened).Read().Scripts.Length);
        Assert.Equal(before, File.Exists(target) ? PhysicalObjectIdentity.Read(target) : null);
        Assert.True(index.Current.IsReady);
        Assert.Equal(expectedPhase == JournalPhase.Quarantined, index.Current.Isolations.Count != 0);
        var second = new StartupJournalRecovery(instance, new(), reopened, roots).Recover(lease);
        Assert.True(second.Current.IsReady);
        output.WriteLine(JsonSerializer.Serialize(new { interruption, identity, stoppedExitCode, stopped, report, final,
            preservedFileIdentity = before, published, isolationCount = index.Current.Isolations.Count }));
    }
}
