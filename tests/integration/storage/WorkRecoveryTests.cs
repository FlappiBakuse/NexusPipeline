using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class WorkRecoveryTests(ITestOutputHelper output)
{
    [Theory(DisplayName = "Actual Killed Cleanup Owners Keep No Effect Completed And Unreceipted Deletion Distinct")]
    [InlineData("accepted", JournalPhase.Committed, FileEffectState.NotExecuted)]
    [InlineData("started", JournalPhase.Committed, FileEffectState.Completed)]
    [InlineData("removed", JournalPhase.Quarantined, FileEffectState.Started)]
    public async Task DistinguishesRecoveryStates(
        string point, JournalPhase expected, FileEffectState firstEffect)
    {
        using var package = new BoundPackageFixture("data-specialized");
        package.Publish();
        var instance = package.Catalog.Instance;
        var roots = new PayloadApplyRoots(Path.Combine(package.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"));
        Directory.CreateDirectory(roots.ExtensionPayloadRoot);
        var input = Path.Combine(package.Root, "work-input.json");
        var report = Path.Combine(package.Root, "work-report.json");
        var removeOperation = Guid.NewGuid();
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Instance = instance, package.Archive, PublicKey = package.Catalog.Signer.ExportSubjectPublicKeyInfo(),
            InitialOperationId = package.OperationId, RemoveOperationId = removeOperation,
            Roots = roots, Point = point, Report = report
        }));
        package.Catalog.Lease.Dispose();
        using var owner = new OwnedCrashProcess();
        var native = owner.Start(["extension-work-recovery", input], package.Root, instance.InstanceId,
            removeOperation, Path.Combine(package.Root, "native-owner.json"));
        var reached = await OwnedCrashProcess.ReadReportAsync(report, TimeSpan.FromSeconds(90));
        Assert.Equal("faultReady", reached.GetProperty("status").GetString());
        Assert.Equal(native.ProcessId, reached.GetProperty("processId").GetInt32());
        var stopped = await owner.StopAsync(); _ = await owner.AwaitExitAsync();
        Assert.True(stopped.CompleteJobStopped);
        var journalId = reached.GetProperty("journalId").GetGuid();
        var producerId = reached.GetProperty("ProducerJournalId").GetGuid();
        var producerPlan = reached.GetProperty("ProducerPlan").Deserialize<BlobReference>()!;
        var workspace = reached.GetProperty("Workspace").GetString()!;
        var producerBytes = reached.GetProperty("ProducerBytes").GetBytesFromBase64();
        var scanner = new FileTreeScanner(new(4097, 268435456));
        var before = scanner.Capture(workspace, default);
        var beforeIds = PhysicalTreeIdentity.Capture(workspace, before);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(package.Root, "inspection"),
            new JournalStartupAdmission(instance, new(), roots));
        var recovery = new StartupJournalRecovery(instance, new(), database, roots).Recover(lease);
        var registered = new RecoveryJournalRegistry(database).ReadJournal(journalId);
        Assert.Equal(expected, registered.Phase);
        Assert.Equal(ExtensionWorkRequest.PageOperation(instance.InstanceId, producerId, null), registered.OperationId);
        var doctor = new RuntimeDoctor(instance, database, recovery);
        Assert.Equal(expected == JournalPhase.Committed ? 0L : 1L, doctor.ReadCounts().PendingOperations);
        var effects = new FileEffectOutbox(database).Read(journalId);
        Assert.Equal(firstEffect, effects[0].State);
        Assert.Equal(producerBytes, new ExtensionPayloadBinding(instance, database, new(), roots).Read(producerId, producerPlan).Latest.Export());
        var installed = new ExtensionInstalledEvidence(instance, database, new(), roots).ReadCurrent("fixture-plugin")!;
        Assert.Equal(2, installed.Metadata.Revision); Assert.False(installed.Metadata.IsInstalled);
        if (point == "started") Assert.False(Directory.Exists(workspace));
        else
        {
            var after = scanner.Capture(workspace, default);
            Assert.Equal(before.Entries, after.Entries); Assert.Equal(beforeIds, PhysicalTreeIdentity.Capture(workspace, after));
        }
        if (point == "removed")
        {
            Assert.Contains(new RecoveryJournalRegistry(database).ReadSnapshot().Isolations, value => value.JournalId == journalId);
            Assert.DoesNotContain(effects, value => value.State == FileEffectState.Completed);
        }
        var bytes = JournalBytes(instance, journalId);
        _ = new StartupJournalRecovery(instance, new(), database, roots).Recover(lease);
        Assert.Equal(expected, new RecoveryJournalRegistry(database).ReadJournal(journalId).Phase);
        Assert.Equal(bytes, JournalBytes(instance, journalId));
        if (point == "accepted")
        {
            var resources = new ResourceLeaseManager(new HostLifecycle(recovery, TimeProvider.System));
            var activator = new ExtensionActivator(new(), lease, resources);
            var service = new WorkCleanupService(instance, database, lease, new(), roots, resources, recovery, activator);
            Assert.True(await service.RunNextPageAsync(producerId, default));
            Assert.False(Directory.Exists(workspace));
            var continuation = Assert.Single(new RecoveryJournalRegistry(database).ReadHistory(),
                item => item.OperationId == ExtensionWorkRequest.PageOperation(instance.InstanceId, producerId, journalId));
            Assert.Equal(JournalPhase.Committed, continuation.Phase);
            Assert.Equal(0L, doctor.ReadCounts().PendingOperations);
            Assert.Equal(bytes, JournalBytes(instance, journalId));
        }
        output.WriteLine(JsonSerializer.Serialize(new
        {
            point, stopped, registered, effectStates = effects.Select(value => new { value.StepId, value.State }),
            expected, scope = "Actual controlled owner Job stopped and cold recovery; unreceipted removal remains quarantined."
        }));
    }

    private static string[] JournalBytes(RuntimeInstance instance, Guid journalId) =>
        Directory.EnumerateFiles(Path.Combine(instance.RuntimeRoot, "journals", journalId.ToString("D")), "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Path.GetFileName(path) + ":" +
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
