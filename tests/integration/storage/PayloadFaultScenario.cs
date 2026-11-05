using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal static class PayloadFaultScenario
{
    internal static async Task RunAsync(PayloadPersistencePoint point, long revision, ITestOutputHelper output)
    {
        using var initial = new BoundPackageFixture("data-specialized");
        initial.Publish();
        using var next = new BoundPackageFixture("data-specialized",
            manifest => manifest["packageVersion"] = "0.6.0", catalog: initial.Catalog);
        var signed = next.Signed(2, entry => entry["packageVersion"] = "0.6.0");
        var owner = initial.Catalog;
        var roots = new PayloadApplyRoots(Path.Combine(initial.Root, "host"), Path.Combine(owner.Instance.RuntimeRoot, "extensions"));
        Directory.CreateDirectory(roots.HostPayloadRoot); Directory.CreateDirectory(roots.ExtensionPayloadRoot);
        var input = new JsonObject
        {
            ["runtimeRoot"] = owner.Instance.RuntimeRoot, ["roots"] = JsonSerializer.SerializeToNode(roots),
            ["initialArchive"] = initial.Archive, ["nextArchive"] = next.Archive,
            ["publicKey"] = Convert.ToBase64String(owner.Signer.ExportSubjectPublicKeyInfo()),
            ["initialOperationId"] = Guid.NewGuid(), ["operationId"] = next.OperationId,
            ["nextPointer"] = Convert.ToBase64String(signed.Pointer), ["nextCatalog"] = Convert.ToBase64String(signed.Catalog),
            ["nextSignature"] = Convert.ToBase64String(signed.Signature), ["stoppedWriters"] = new JsonArray()
        };
        owner.Lease.Dispose();
        var stopped = new List<OwnedCrashStop>();
        Guid journalId;
        using (var creator = new OwnedCrashProcess())
        {
            var report = Path.Combine(initial.Root, "accept-report.json");
            var identity = Start(creator, "accept-extension", WriteInput("accept"), PayloadPersistencePoint.AcceptanceCommitted.ToString(), 1,
                report, next.OperationId, "accept");
            var ready = await OwnedCrashProcess.ReadReportAsync(report, TimeSpan.FromSeconds(45));
            Assert.Equal("faultReady", ready.GetProperty("status").GetString());
            Assert.Equal(identity.ProcessId, ready.GetProperty("processId").GetInt32());
            journalId = ready.GetProperty("journalId").GetGuid();
            stopped.Add(await creator.StopAsync());
        }
        var registered = new RecoveryJournalRegistry(owner.Database).ReadJournal(journalId);
        var reference = new BlobReference(registered.PlanPath,
            new FileInfo(Path.Combine(owner.Instance.RuntimeRoot, registered.PlanPath)).Length, registered.PlanDigest);
        var scanner = new FileTreeScanner(new(4096, 268435456));
        var plan = new PayloadPlanCodec(owner.Instance, owner.Contracts, next.Blobs, roots).Read(reference);
        input["plan"] = JsonSerializer.SerializeToNode(reference);
        Assert.Equal(next.OperationId, plan.OperationId);
        Assert.NotNull(plan.ExtensionSource);
        Assert.Equal(PayloadAction.Upgrade, plan.Action);
        if (point != PayloadPersistencePoint.AcceptanceCommitted)
        {
            using var recovering = new OwnedCrashProcess();
            var report = Path.Combine(initial.Root, "fault-report.json");
            var identity = Start(recovering, "recover", WriteInput("fault"), point.ToString(), revision, report, journalId, "fault");
            var ready = await OwnedCrashProcess.ReadReportAsync(report);
            Assert.Equal("faultReady", ready.GetProperty("status").GetString());
            Assert.Equal(identity.ProcessId, ready.GetProperty("processId").GetInt32());
            Assert.Equal(journalId, ready.GetProperty("journalId").GetGuid());
            Assert.Equal(revision, ready.GetProperty("revision").GetInt64());
            stopped.Add(await recovering.StopAsync());
        }
        var first = await Recover("first");
        Assert.Equal((revision == 3 ? JournalRecoveryResult.AlreadyCompleted : JournalRecoveryResult.NotExecuted).ToString(),
            first.GetProperty("result").GetString());
        var snapshot = new RecoveryJournalRegistry(owner.Database).ReadSnapshot();
        var current = Assert.Single(snapshot.Journals, item => item.JournalId == journalId);
        Assert.Equal(JournalPhase.Committed, current.Phase);
        Assert.DoesNotContain(snapshot.Isolations, item => item.JournalId == journalId);
        Assert.All(snapshot.FileEffects.Where(item => item.JournalId == journalId), item => Assert.Equal(FileEffectState.NotExecuted, item.State));
        Assert.Equal(EffectGateState.Sealed, new JournalEffectGate(owner.Database).Read(journalId).State);
        using (var queryLease = InstanceLease.Acquire(owner.Instance, owner.Contracts))
        {
            var query = new ExtensionQueryService(owner.Instance, queryLease, owner.Database, owner.Contracts, roots);
            var state = query.Read(next.OperationId, "fixture-payload")!;
            Assert.Equal(ExtensionApplyOutcome.NotExecuted, state.Outcome); Assert.Equal("completed", state.Operation.Status);
            Assert.Equal(journalId, state.Operation.InitialJournalId); Assert.Equal(JournalPhase.Committed, state.Phase);
            Assert.Null(state.ResultInstalledRevision); Assert.Null(state.ResultIsInstalled); Assert.Null(state.HistoricalActivationId);
            Assert.Null(query.Read(next.OperationId, "another-subject"));
        }
        var actual = scanner.Capture(plan.TargetPath, default);
        Assert.Equal(plan.Before.Snapshot.Entries, actual.Entries);
        Assert.Equal(plan.Before.OriginalObjects, PhysicalTreeIdentity.Capture(plan.TargetPath, actual));
        var beforeRepeat = JournalBytes();
        var second = await Recover("second");
        Assert.Equal(JournalRecoveryResult.AlreadyCompleted.ToString(), second.GetProperty("result").GetString());
        Assert.Equal(beforeRepeat, JournalBytes());
        Assert.Equal(snapshot.Revision, new RecoveryJournalRegistry(owner.Database).ReadSnapshot().Revision);
        output.WriteLine(JsonSerializer.Serialize(new { point, revision, journalId, stopped, first, second,
            scope = "Real disabled trusted installation and opaque upgrade acceptance; actual creator/recoverer Job-family kills, no extension effect or activation replay" }));

        string WriteInput(string name)
        {
            input["stoppedWriters"] = new JsonArray(stopped.Select(item => JsonSerializer.SerializeToNode(new
                { completeJobStopped = item.CompleteJobStopped, identity = item.Identity, family = item.Family, accounting = item.Accounting })).ToArray());
            var path = Path.Combine(initial.Root, name + "-input.json");
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(input)); return path;
        }

        NexusPipeline.Platform.Processes.OwnedWorkerIdentity Start(OwnedCrashProcess process, string mode, string inputPath,
            string target, long targetRevision, string report, Guid nativeScope, string name) =>
            process.Start(["payload", mode, inputPath, target, targetRevision.ToString(CultureInfo.InvariantCulture), report],
                initial.Root, owner.Instance.InstanceId, nativeScope, Path.Combine(initial.Root, name + "-identity.json"));

        async Task<JsonElement> Recover(string name)
        {
            using var process = new OwnedCrashProcess();
            var report = Path.Combine(initial.Root, name + "-report.json");
            Start(process, "recover", WriteInput(name), "none", 0, report, journalId, name);
            var result = await OwnedCrashProcess.ReadReportAsync(report);
            Assert.Equal(0, await process.AwaitExitAsync());
            var stop = await process.StopAsync();
            File.WriteAllBytes(Path.Combine(initial.Root, name + "-family.json"), JsonSerializer.SerializeToUtf8Bytes(stop));
            Assert.Equal("completed", result.GetProperty("status").GetString()); return result;
        }

        string[] JournalBytes() => Directory.EnumerateFiles(Path.Combine(owner.Instance.RuntimeRoot, "journals"), "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(owner.Instance.RuntimeRoot, path) + ":" +
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    }
}
