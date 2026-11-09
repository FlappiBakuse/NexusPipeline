using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ConfigurationCrashTests(ITestOutputHelper output)
{
    [Theory(DisplayName = "Killing The Actual Accepting Process Before Its Initial Journal Preserves Accepted Identity And Proves No Effects On Restart")]
    [InlineData("missing")]
    [InlineData("file")]
    [InlineData("directory")]
    public async Task RecoversUnenteredAcceptance(string shape)
    {
        var fixture = Create(shape, initializeJournal: false);
        using var accepting = new OwnedWorkerJob();
        var faultReport = Path.Combine(fixture.Root, "accepted-fault.json");
        var identity = Start(accepting, fixture, "accept-initial", fixture.InputPath, "accepted-before-initial-journal", faultReport, "accepting-process.json");
        var fault = await ReadReport(faultReport);
        Assert.Equal("faultReady", fault.GetProperty("status").GetString());
        Assert.Equal(identity.ProcessId, fault.GetProperty("processId").GetInt32());
        var family = ObserveFamily(accepting);
        Assert.True(await accepting.StopAsync(TimeSpan.FromSeconds(5)));
        AssertJournal(fixture, JournalPhase.Prepared);
        Assert.ThrowsAny<IOException>(() => fixture.Journals.Read(fixture.Plan.JournalId));
        AssertOriginal(fixture);
        var faultFiles = CaptureFiles(fixture);
        fixture.Input["stoppedWriter"] = JsonSerializer.SerializeToNode(new
            { completeJobStopped = true, identity, family, accounting = accepting.GetAccounting() });
        var input = Path.Combine(fixture.Root, "initial-recovery-input.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(fixture.Input));
        using var recovering = new OwnedWorkerJob();
        var report = Path.Combine(fixture.Root, "initial-recovery-report.json");
        Start(recovering, fixture, "recover-initial", input, "none", report, "recovering-initial-process.json");
        var result = await ReadReport(report);
        Assert.True(await recovering.StopAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("notExecuted", result.GetProperty("status").GetString());
        var journal = AssertJournal(fixture, JournalPhase.Committed);
        Assert.Empty(fixture.Registry.ReadSnapshot().Isolations);
        var effects = fixture.Outbox.Read(fixture.Plan.JournalId);
        Assert.NotEmpty(effects);
        Assert.All(effects, effect => Assert.Equal(FileEffectState.NotExecuted, effect.State));
        AssertOriginal(fixture);
        output.WriteLine(JsonSerializer.Serialize(new { faultPoint = "accepted-before-initial-journal", shape,
            acceptingProcess = identity, ownedFamily = family, faultFiles, finalFiles = CaptureFiles(fixture), finalJournal = journal }));
    }

    public static IEnumerable<object[]> PhysicalFaultCases()
    {
        string[] points = ["before-prepare", "prepared-before-workspace-receipt", "after-workspace-receipt", "before-preserve-original",
            "preserved-before-receipt", "after-preserve-receipt", "before-install-candidate", "installed-before-receipt", "after-install-receipt"];
        foreach (var point in points)
            yield return ["file", point, point is "before-prepare" or "after-workspace-receipt"
                or "after-install-receipt" or "prepared-before-workspace-receipt"];
        yield return ["missing", "installed-before-receipt", false];
        yield return ["directory", "installed-before-receipt", false];
    }

    [Theory(DisplayName = "Owned Process Kill And New Process Recovery Preserve Original Bytes Or Explicitly Quarantine The Unproven Workspace")]
    [MemberData(nameof(PhysicalFaultCases))]
    public async Task PreservesKilledWriters(string shape, string point, bool repeat)
    {
        var fixture = Create(shape);
        var executeReport = Path.Combine(fixture.Root, "execute-report.json");
        using var oldJob = new OwnedWorkerJob();
        var old = Start(oldJob, fixture, "execute", fixture.InputPath, point, executeReport, "execute-identity.json");
        var ready = await ReadReport(executeReport);
        Assert.Equal("faultReady", ready.GetProperty("status").GetString());
        Assert.Equal(point, ready.GetProperty("faultPoint").GetString());
        Assert.Equal(old.ProcessId, ready.GetProperty("processId").GetInt32());
        var family = ObserveFamily(oldJob);
        Assert.Contains(family, member => member.ProcessId == old.ProcessId && member.CreatedAtUtc == old.CreatedAtUtc);
        File.WriteAllBytes(Path.Combine(fixture.Root, "owned-family-before-kill.json"), JsonSerializer.SerializeToUtf8Bytes(family));
        Assert.True(await oldJob.StopAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0u, oldJob.GetAccounting().ActiveProcesses);
        var faultFiles = CaptureFiles(fixture);
        File.WriteAllBytes(Path.Combine(fixture.Root, "fault-files-before-recovery.json"), JsonSerializer.SerializeToUtf8Bytes(faultFiles));
        var stopped = JsonSerializer.SerializeToNode(new { completeJobStopped = true, identity = old, family, accounting = oldJob.GetAccounting() });
        fixture.Input["stoppedWriter"] = stopped;
        var recoveryInput = Path.Combine(fixture.Root, "recovery-input.json");
        File.WriteAllBytes(recoveryInput, JsonSerializer.SerializeToUtf8Bytes(fixture.Input));
        var recoveryReport = Path.Combine(fixture.Root, "recovery-report.json");
        using var recoveryJob = new OwnedWorkerJob();
        var recovery = Start(recoveryJob, fixture, "recover", recoveryInput, "none", recoveryReport, "recovery-identity.json");
        var result = await ReadReport(recoveryReport);
        Assert.True(await recoveryJob.StopAsync(TimeSpan.FromSeconds(5)));
        Assert.NotEqual((old.ProcessId, old.CreatedAtUtc), (recovery.ProcessId, recovery.CreatedAtUtc));
        var expectedStatus = point == "before-prepare" ? "notExecuted" : point == "prepared-before-workspace-receipt" ? "quarantined" : "restored";
        Assert.Equal(expectedStatus, result.GetProperty("status").GetString());
        var actual = AssertOriginal(fixture);
        var snapshot = fixture.Registry.ReadSnapshot();
        var outbox = fixture.Outbox.Read(fixture.Plan.JournalId);
        var journal = AssertJournal(fixture, expectedStatus == "quarantined" ? JournalPhase.Quarantined : JournalPhase.Committed);
        if (expectedStatus == "quarantined") AssertIsolation(fixture, snapshot);
        else
        {
            Assert.Empty(snapshot.Isolations);
            Assert.NotEmpty(outbox);
            Assert.All(outbox, effect => Assert.Contains(effect.State, new[] { FileEffectState.Completed, FileEffectState.NotExecuted }));
        }
        if (repeat)
        {
            var history = fixture.Journals.ReadAllRevisions(journal.JournalId).Select(item => item.Export()).ToArray();
            var repeated = await RecoverAgain(fixture, recoveryInput);
            Assert.Equal(expectedStatus == "quarantined" ? "quarantined" : "alreadyCompleted", repeated.GetProperty("status").GetString());
            Assert.Equal(journal.Revision, AssertJournal(fixture, journal.Phase).Revision);
            Assert.Equal(history, fixture.Journals.ReadAllRevisions(journal.JournalId).Select(item => item.Export()));
            Assert.Equal(outbox, fixture.Outbox.Read(journal.JournalId));
            Assert.Equal(snapshot.Isolations, fixture.Registry.ReadSnapshot().Isolations);
            AssertOriginal(fixture);
        }
        output.WriteLine(JsonSerializer.Serialize(new { faultPoint = point, shape, processKilled = old, ownedFamily = family, recoveryProcess = recovery,
            original = fixture.Plan.Before, faultFiles, finalFiles = CaptureFiles(fixture), recovered = actual, journal, fileEffects = outbox, isolation = snapshot.Isolations, result = expectedStatus,
            scope = "Controlled process interruption; physical swap primitives and durable SQL/journal evidence; not physical power loss or complete Host startup recovery" }));
    }

    private static IReadOnlyList<ObservedMember> ObserveFamily(OwnedWorkerJob job)
    {
        var members = new List<ObservedMember>();
        var processes = job.ActiveProcessIds();
        foreach (var pid in processes)
        {
            using var process = Process.GetProcessById(pid);
            var image = process.MainModule?.FileName ?? throw new IOException("Owned member image is unavailable");
            using var input = File.OpenRead(image);
            members.Add(new(pid, process.StartTime.ToUniversalTime(), image, Convert.ToHexStringLower(SHA256.HashData(input))));
        }
        if (!processes.Order().SequenceEqual(job.ActiveProcessIds().Order())) throw new IOException("Owned family changed during observation");
        return members.AsReadOnly();
    }

    private sealed record ObservedMember(int ProcessId, DateTime CreatedAtUtc, string ImagePath, string ImageSha256);

    [Fact(DisplayName = "A Still Running Registered Writer Cannot Be Reclassified As Stopped By An Input Flag")]
    public async Task RejectsLivingWriters()
    {
        var fixture = Create("file");
        var executeReport = Path.Combine(fixture.Root, "execute-report.json");
        using var oldJob = new OwnedWorkerJob();
        var old = Start(oldJob, fixture, "execute", fixture.InputPath, "after-install-receipt", executeReport, "execute-identity.json");
        Assert.Equal("faultReady", (await ReadReport(executeReport)).GetProperty("status").GetString());
        fixture.Input["stoppedWriter"] = JsonSerializer.SerializeToNode(new
            { completeJobStopped = true, identity = old, family = ObserveFamily(oldJob), accounting = new WorkerJobAccounting(0, 1, 0) });
        var input = Path.Combine(fixture.Root, "recovery-input.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(fixture.Input));
        var report = Path.Combine(fixture.Root, "recovery-report.json");
        using var recoveryJob = new OwnedWorkerJob();
        Start(recoveryJob, fixture, "recover", input, "none", report, "recovery-identity.json");
        var result = await ReadReport(report);
        Assert.Equal("refused", result.GetProperty("status").GetString());
        Assert.Contains(old.ProcessId, oldJob.ActiveProcessIds());
        Assert.Equal(fixture.Plan.Candidate.Snapshot.Entries, fixture.Scanner.Capture(fixture.Plan.TargetPath, CancellationToken.None).Entries);
        Assert.True(await recoveryJob.StopAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await oldJob.StopAsync(TimeSpan.FromSeconds(5)));
        AssertIsolation(fixture, fixture.Registry.ReadSnapshot());
    }

    [Fact(DisplayName = "Exited Primary Process Does Not Authorize Restoration While Another Registered Writer Is Alive")]
    public async Task RequiresStoppedFamily()
    {
        var fixture = Create("file");
        var executeReport = Path.Combine(fixture.Root, "execute-report.json");
        using var job = new OwnedWorkerJob();
        var primary = Start(job, fixture, "execute", fixture.InputPath, "after-install-receipt", executeReport, "execute-identity.json");
        Assert.Equal("faultReady", (await ReadReport(executeReport)).GetProperty("status").GetString());
        var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var ready = Path.Combine(fixture.Root, "necessary-writer-ready.json");
        var writer = job.Start(executable, [ready, "necessary-writer"], fixture.Root, fixture.Plan.InstanceId, fixture.Plan.JournalId, identity =>
            PersistIdentity(Path.Combine(fixture.Root, "necessary-writer-identity.json"), identity));
        await ReadReport(ready);
        var family = ObserveFamily(job);
        using (var process = Process.GetProcessById(primary.ProcessId))
        {
            Assert.Equal(primary.CreatedAtUtc, process.StartTime.ToUniversalTime());
            Assert.Equal(primary.ImagePath, process.MainModule!.FileName);
            Assert.Contains(primary.ProcessId, job.ActiveProcessIds());
            Assert.Equal(primary.ImageSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(primary.ImagePath))));
            process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync();
        }
        Assert.Contains(writer.ProcessId, job.ActiveProcessIds());
        fixture.Input["stoppedWriter"] = JsonSerializer.SerializeToNode(new
            { completeJobStopped = true, identity = primary, family, accounting = new WorkerJobAccounting(0, (uint)family.Count, 0) });
        var input = Path.Combine(fixture.Root, "recovery-input.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(fixture.Input));
        var report = Path.Combine(fixture.Root, "recovery-report.json");
        using var recovery = new OwnedWorkerJob();
        Start(recovery, fixture, "recover", input, "none", report, "recovery-identity.json");
        Assert.Equal("refused", (await ReadReport(report)).GetProperty("status").GetString());
        Assert.Contains(writer.ProcessId, job.ActiveProcessIds());
        Assert.Equal(fixture.Plan.Candidate.Snapshot.Entries, fixture.Scanner.Capture(fixture.Plan.TargetPath, CancellationToken.None).Entries);
        AssertIsolation(fixture, fixture.Registry.ReadSnapshot());
        Assert.True(await recovery.StopAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(5)));
    }

    private static OwnedWorkerIdentity Start(OwnedWorkerJob job, Fixture fixture, string mode, string input, string point, string report, string identityFile)
    {
        var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new InvalidOperationException();
        return job.Start(executable, ["configuration", mode, input, point, report], fixture.Root, fixture.Plan.InstanceId, fixture.Plan.JournalId,
            identity => PersistIdentity(Path.Combine(fixture.Root, identityFile), identity));
    }

    private static async Task<JsonElement> RecoverAgain(Fixture fixture, string input)
    {
        var report = Path.Combine(fixture.Root, "recovery-report-2.json");
        using var job = new OwnedWorkerJob();
        Start(job, fixture, "recover", input, "none", report, "recovery-identity-2.json");
        var result = await ReadReport(report);
        var exit = Stopwatch.StartNew();
        while (job.ActiveProcessIds().Count != 0 && exit.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        Assert.Empty(job.ActiveProcessIds());
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(5)));
        return result;
    }

    public static IEnumerable<object[]> RecoveryFaultCases()
    {
        foreach (var point in new[] { "recover-reverted-journal-flushed", "recover-reverted-sql-projected",
            "recover-committed-journal-flushed", "recover-committed-sql-projected" })
            yield return ["file", point, "after-install-receipt"];
        yield return ["missing", "recover-reverted-journal-flushed", "after-install-receipt"];
        yield return ["directory", "recover-reverted-journal-flushed", "after-install-receipt"];
        yield return ["file", "recover-committed-journal-flushed", "before-prepare"];
        yield return ["file", "recover-committed-sql-projected", "before-prepare"];
    }

    [Theory(DisplayName = "Killing Recovery Between Journal And Sql Commit Reconciles Without Repeating Physical Effects")]
    [MemberData(nameof(RecoveryFaultCases))]
    public async Task ReconcilesInterruptedCommit(string shape, string point, string initialPoint)
    {
        var fixture = Create(shape);
        var executeReport = Path.Combine(fixture.Root, "execute-report.json");
        using var execute = new OwnedWorkerJob();
        var original = Start(execute, fixture, "execute", fixture.InputPath, initialPoint, executeReport, "execute-identity.json");
        Assert.Equal("faultReady", (await ReadReport(executeReport)).GetProperty("status").GetString());
        var executeFamily = ObserveFamily(execute);
        Assert.True(await execute.StopAsync(TimeSpan.FromSeconds(5)));
        fixture.Input["stoppedWriter"] = JsonSerializer.SerializeToNode(new
            { completeJobStopped = true, identity = original, family = executeFamily, accounting = execute.GetAccounting() });
        var firstInput = Path.Combine(fixture.Root, "recovery-input-first.json");
        File.WriteAllBytes(firstInput, JsonSerializer.SerializeToUtf8Bytes(fixture.Input));
        var faultReport = Path.Combine(fixture.Root, "recovery-fault.json");
        using var firstRecovery = new OwnedWorkerJob();
        var recoveryIdentity = Start(firstRecovery, fixture, "recover", firstInput, point, faultReport, "interrupted-recovery-identity.json");
        var fault = await ReadReport(faultReport);
        Assert.Equal("faultReady", fault.GetProperty("status").GetString());
        Assert.Equal(point, fault.GetProperty("faultPoint").GetString());
        var recoveringFamily = ObserveFamily(firstRecovery);
        Assert.True(await firstRecovery.StopAsync(TimeSpan.FromSeconds(5)));
        var faultFiles = CaptureFiles(fixture);
        File.WriteAllBytes(Path.Combine(fixture.Root, "fault-files-before-recovery.json"), JsonSerializer.SerializeToUtf8Bytes(faultFiles));
        var restoredBeforeRestart = fixture.Scanner.Capture(fixture.Plan.TargetPath, CancellationToken.None);
        var identitiesBeforeRestart = PhysicalTreeIdentity.Capture(fixture.Plan.TargetPath, restoredBeforeRestart);
        fixture.Input["stoppedWriter"] = JsonSerializer.SerializeToNode(new
            { completeJobStopped = true, identity = recoveryIdentity, family = recoveringFamily, accounting = firstRecovery.GetAccounting() });
        var secondInput = Path.Combine(fixture.Root, "recovery-input-second.json");
        File.WriteAllBytes(secondInput, JsonSerializer.SerializeToUtf8Bytes(fixture.Input));
        var result = await RecoverAgain(fixture, secondInput);
        Assert.Equal(point.StartsWith("recover-committed", StringComparison.Ordinal) ? "alreadyCompleted" : "restored", result.GetProperty("status").GetString());
        var actual = AssertOriginal(fixture);
        Assert.Equal(identitiesBeforeRestart, PhysicalTreeIdentity.Capture(fixture.Plan.TargetPath, actual));
        var snapshot = fixture.Registry.ReadSnapshot();
        var journal = AssertJournal(fixture, JournalPhase.Committed);
        Assert.Empty(snapshot.Isolations);
        var outbox = fixture.Outbox.Read(fixture.Plan.JournalId);
        Assert.NotEmpty(outbox);
        Assert.All(outbox, effect => Assert.Contains(effect.State, new[] { FileEffectState.Completed, FileEffectState.NotExecuted }));
        output.WriteLine(JsonSerializer.Serialize(new { faultPoint = point, shape, initialPoint, recoveryKilled = recoveryIdentity, ownedFamily = recoveringFamily,
            faultFiles, finalFiles = CaptureFiles(fixture), finalJournal = journal, original = fixture.Plan.Before,
            restored = actual, result = result.GetProperty("status").GetString() }));
    }

    private static object CaptureFiles(Fixture fixture)
    {
        var runtimePath = Path.Combine(fixture.Root, "runtime");
        var configurationPath = Path.GetDirectoryName(fixture.Plan.TargetPath)!;
        var runtime = fixture.Scanner.Capture(runtimePath, CancellationToken.None);
        var configuration = fixture.Scanner.Capture(configurationPath, CancellationToken.None);
        return new { fixtureRoot = fixture.Root, runtime, runtimeObjects = PhysicalTreeIdentity.Capture(runtimePath, runtime),
            configuration, configurationObjects = PhysicalTreeIdentity.Capture(configurationPath, configuration) };
    }

    private static FileTreeSnapshot AssertOriginal(Fixture fixture)
    {
        var actual = fixture.Scanner.Capture(fixture.Plan.TargetPath, CancellationToken.None);
        Assert.Equal(fixture.Plan.Before.Snapshot.Shape, actual.Shape);
        Assert.Equal(fixture.Plan.Before.Snapshot.Entries, actual.Entries);
        Assert.Equal(fixture.Plan.Before.OriginalObjects, PhysicalTreeIdentity.Capture(fixture.Plan.TargetPath, actual));
        return actual;
    }

    private static RegisteredJournal AssertJournal(Fixture fixture, JournalPhase phase)
    {
        var journal = fixture.Registry.ReadOperationJournal(fixture.Plan.OperationId);
        Assert.Equal(fixture.Plan.JournalId, journal.JournalId);
        Assert.Equal(fixture.Plan.Kind, journal.Kind);
        Assert.Equal(fixture.PlanReference.Sha256, journal.PlanDigest);
        Assert.Equal(phase, journal.Phase);
        return journal;
    }

    private static void AssertIsolation(Fixture fixture, RecoveryRegistrySnapshot snapshot)
    {
        var isolation = Assert.Single(snapshot.Isolations);
        Assert.Equal(fixture.Plan.JournalId, isolation.JournalId);
        Assert.Equal("configuration:fixture", isolation.ResourceKey);
    }

    private static void PersistIdentity(string path, OwnedWorkerIdentity identity)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        file.Write(JsonSerializer.SerializeToUtf8Bytes(identity));
        file.Flush(flushToDisk: true);
    }

    private static async Task<JsonElement> ReadReport(string path)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (File.Exists(path))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                    return document.RootElement.Clone();
                }
                catch (JsonException) { }
                catch (IOException) { }
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("Controlled configuration process did not produce its bounded report");
    }

    private static Fixture Create(string shape, bool initializeJournal = true)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException(), "crash-" + Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var targetParent = Path.Combine(root, "configuration");
        Directory.CreateDirectory(targetParent);
        var target = Path.Combine(targetParent, "target");
        if (shape == "file") File.WriteAllText(target, "original\n中文");
        if (shape == "directory")
        {
            Directory.CreateDirectory(Path.Combine(target, "nested", "empty"));
            File.WriteAllText(Path.Combine(target, "nested", "configuration"), "original\n中文");
        }
        var candidate = Path.Combine(root, "candidate-input");
        File.WriteAllText(candidate, "candidate\n字节");
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var archive = new FileTreeArchive(scanner, blobs);
        var operation = new OperationIdentity(Guid.NewGuid(), "fixture-subject", new string('a', 64), "configuration.exchange");
        var plan = new FrozenConfigurationExchange(instance.InstanceId, operation.OperationId, Guid.NewGuid(), JournalKind.ConfigExchange,
            target, PhysicalObjectIdentity.Read(targetParent), archive.Capture(target, CancellationToken.None), archive.Capture(candidate, CancellationToken.None));
        using var preparedPlan = new ConfigurationPlanCodec(instance, contracts, blobs).Freeze(plan);
        var reference = preparedPlan.Reference;
        var registry = new RecoveryJournalRegistry(database);
        var actions = new Dictionary<string, FileEffectAction>
        {
            ["preserve-original"] = FileEffectAction.PreserveOriginal, ["install-candidate"] = FileEffectAction.InstallCandidate,
            ["retire-candidate"] = FileEffectAction.RetireCandidate, ["restore-original"] = FileEffectAction.RestoreOriginal
        };
        var effects = actions.Select(item => new FileEffect(plan.JournalId, item.Key, item.Value, FileEffectState.Pending, reference.Sha256, null)).ToArray();
        if (initializeJournal) registry.AcceptPrepared(operation, new(plan.JournalId, operation.OperationId, plan.Kind, JournalPhase.Prepared, 1,
            reference.RelativePath, reference.Sha256, ["configuration:fixture"]), effects, DateTimeOffset.UtcNow);
        var journal = new
        {
            format = "nexus.execution.journal", version = 1, instanceId = instance.InstanceId, journalId = plan.JournalId, operationId = operation.OperationId,
            operationKind = "configExchange", phase = "prepared", revision = 1, updatedAt = "2026-10-19T00:00:00Z",
            planPath = reference.RelativePath, planDigest = reference.Sha256, resourceKeys = new[] { "configuration:fixture" }, participants = Array.Empty<object>(), receipts = Array.Empty<object>()
        };
        var journals = new JournalStore(instance, contracts);
        if (initializeJournal) journals.Create(JsonSerializer.SerializeToUtf8Bytes(journal));
        var input = new JsonObject { ["runtimeRoot"] = instance.RuntimeRoot, ["plan"] = JsonSerializer.SerializeToNode(reference) };
        var inputPath = Path.Combine(root, "execute-input.json");
        File.WriteAllBytes(inputPath, JsonSerializer.SerializeToUtf8Bytes(input));
        return new(root, inputPath, input, plan, reference, scanner, registry, new(database), journals);
    }

    private sealed record Fixture(string Root, string InputPath, JsonObject Input, FrozenConfigurationExchange Plan,
        BlobReference PlanReference, FileTreeScanner Scanner, RecoveryJournalRegistry Registry, FileEffectOutbox Outbox, JournalStore Journals);
}
