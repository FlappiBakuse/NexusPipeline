using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
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
public sealed class StartupAdmissionTests(ITestOutputHelper output)
{
    [Theory(DisplayName = "Actual Host Rejects Unrecognized Or Unrecovered Plans Before Changing Original Database Or Wal")]
    [InlineData("unknown-plan-version", false)]
    [InlineData("unknown-plan-version", true)]
    [InlineData("unbound-plan", false)]
    [InlineData("unbound-plan", true)]
    [InlineData("unknown-participant", false)]
    [InlineData("unknown-participant", true)]
    [InlineData("unsupported-kind", false)]
    [InlineData("unsupported-kind", true)]
    [InlineData("known-active", false)]
    [InlineData("known-active", true)]
    public async Task PreservesUnrecognizedPlans(string fault, bool retainWal)
    {
        var fixture = Create(fault, retainWal);
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        using var job = new OwnedWorkerJob();
        var identity = Start(job, fixture, 0);
        using var process = Process.GetProcessById(identity.ProcessId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(1, process.ExitCode);
        await Stopped(job);
        var after = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.Equal(before, after);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(fixture.Target));
        output.WriteLine(JsonSerializer.Serialize(new { faultPoint = fault, retainWal, identity, before, after,
            nativeExitCode = process.ExitCode, accounting = job.GetAccounting(), allProcessesStopped = job.ActiveProcessIds().Count == 0 }));
    }

    [Fact(DisplayName = "A Completed Typed Configuration Plan Can Be Admitted Without Rewriting Its Original File Group")]
    public void AdmitsCompletedPlans()
    {
        var fixture = Create("known-active", retainWal: false);
        Complete(fixture);
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        var reopened = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "diagnostics"),
            new JournalStartupAdmission(fixture.Instance, fixture.Contracts));
        Assert.Equal(fixture.Database.InstanceId, reopened.InstanceId);
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Root, "diagnostics")));
        for (var repeat = 0; repeat < 2; repeat++)
            _ = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "diagnostics"),
                new JournalStartupAdmission(fixture.Instance, fixture.Contracts));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Root, "diagnostics")));
    }

    [Fact(DisplayName = "Actual Host Serves The Current Control Plane After A Typed Configuration Journal Has Completed")]
    public async Task ServesRecoveredState()
    {
        var fixture = Create("known-active", retainWal: false);
        Complete(fixture);
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var job = new OwnedWorkerJob();
        var identity = Start(job, fixture, port);
        using var process = Process.GetProcessById(identity.ProcessId);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromMilliseconds(250) };
        JsonDocument? capability = null;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10) && !process.HasExited)
        {
            try
            {
                using var response = await http.GetAsync("control/v1/capabilities");
                response.EnsureSuccessStatusCode();
                capability = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
                break;
            }
            catch (HttpRequestException) { await Task.Delay(25); }
            catch (TaskCanceledException) { await Task.Delay(25); }
        }
        using (capability)
        {
            Assert.NotNull(capability);
            Assert.Equal(fixture.Instance.InstanceId, capability.RootElement.GetProperty("instanceId").GetGuid());
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(fixture.Target));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(5)));
        output.WriteLine(JsonSerializer.Serialize(new { identity, servedCurrentControlPlane = true, forcedFixtureStop = true,
            allProcessesStopped = job.ActiveProcessIds().Count == 0, scope = "Startup admission only; fixture termination is not graceful desktop shutdown evidence" }));
    }

    private static void Complete(Fixture fixture)
    {
        var session = new ConfigurationRecoverySession(fixture.Instance, fixture.JournalId, fixture.Contracts, fixture.Database,
            () => new(fixture.Instance.InstanceId, fixture.JournalId, true, WriterState.Stopped));
        Assert.Equal(ConfigurationRecoveryResult.NotExecuted, new ConfigurationRecovery(session).Recover());
    }

    private static Fixture Create(string fault, bool retainWal)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        using var lease = InstanceLease.Acquire(instance, contracts);
        var database = StateDatabase.CreateFresh(instance);
        var target = Path.Combine(root, "target.config");
        File.WriteAllBytes(target, [1, 2, 3]);
        var candidate = Path.Combine(root, "candidate.config");
        File.WriteAllBytes(candidate, [4, 5]);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), blobs);
        var operation = new OperationIdentity(Guid.NewGuid(), "fixture", new string('a', 64), "configuration.exchange");
        var plan = new FrozenConfigurationExchange(instance.InstanceId, operation.OperationId, Guid.NewGuid(), JournalKind.ConfigExchange,
            target, PhysicalObjectIdentity.Read(root), archive.Capture(target, default), archive.Capture(candidate, default));
        using var preparedPlan = new ConfigurationPlanCodec(instance, contracts, blobs).Freeze(plan);
        var reference = preparedPlan.Reference;
        if (fault is "unknown-plan-version" or "unbound-plan")
        {
            var changed = JsonNode.Parse(File.ReadAllBytes(Path.Combine(instance.RuntimeRoot, reference.RelativePath)))!;
            if (fault == "unknown-plan-version") changed["version"] = 17;
            else changed["operationId"] = Guid.NewGuid().ToString("D");
            reference = blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(changed));
        }
        var kind = fault == "unsupported-kind" ? JournalKind.FileCleanup : plan.Kind;
        var registered = new RegisteredJournal(plan.JournalId, operation.OperationId, kind, JournalPhase.Prepared, 1,
            reference.RelativePath, reference.Sha256, ["configuration:startup-fixture"]);
        Dictionary<string, byte[]>? group = null;
        using (var anchor = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False"))
        {
            anchor.Open();
            using var read = anchor.CreateCommand();
            read.CommandText = "SELECT count(*) FROM operations;";
            read.ExecuteScalar();
            var effect = new FileEffect(plan.JournalId, kind == JournalKind.FileCleanup ? "remove-owned" : "install-candidate",
                kind == JournalKind.FileCleanup ? FileEffectAction.RemoveOwned : FileEffectAction.InstallCandidate, FileEffectState.Pending, reference.Sha256, null);
            new RecoveryJournalRegistry(database).AcceptPrepared(operation, registered, [effect], DateTimeOffset.UtcNow);
            WriteJournal(instance, contracts, registered, fault);
            if (retainWal) group = new[] { "", "-wal", "-shm" }.ToDictionary(suffix => suffix, suffix => Quiescent(database.DatabasePath + suffix));
        }
        if (group is not null) foreach (var (suffix, bytes) in group) File.WriteAllBytes(database.DatabasePath + suffix, bytes);
        return new(root, instance, contracts, database, target, plan.JournalId);
    }

    private static void WriteJournal(RuntimeInstance instance, ContractReader contracts, RegisteredJournal journal, string fault)
    {
        var document = new JsonObject
        {
            ["format"] = "nexus.execution.journal", ["version"] = 1, ["instanceId"] = instance.InstanceId,
            ["journalId"] = journal.JournalId, ["operationId"] = journal.OperationId,
            ["operationKind"] = journal.Kind == JournalKind.FileCleanup ? "fileCleanup" : "configExchange",
            ["phase"] = "prepared", ["revision"] = 1, ["updatedAt"] = "2026-10-19T00:00:00Z", ["planPath"] = journal.PlanPath,
            ["planDigest"] = journal.PlanDigest, ["resourceKeys"] = new JsonArray("configuration:startup-fixture"),
            ["participants"] = new JsonArray(), ["receipts"] = new JsonArray()
        };
        if (fault == "unknown-participant") document["participants"] = new JsonArray(new JsonObject
        {
            ["participantId"] = Guid.NewGuid(), ["kind"] = "directory", ["resourceKey"] = "configuration:startup-fixture",
            ["identity"] = new JsonObject { ["format"] = "nexus.configuration.workspace", ["version"] = 17 }
        });
        new JournalStore(instance, contracts).Create(JsonSerializer.SerializeToUtf8Bytes(document));
    }

    private static OwnedWorkerIdentity Start(OwnedWorkerJob job, Fixture fixture, int port) => job.Start(
        Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!, ["serve", "--runtime-root", fixture.Instance.RuntimeRoot, "--port", port.ToString()],
        fixture.Root, fixture.Instance.InstanceId, Guid.NewGuid(), identity =>
            File.WriteAllBytes(Path.Combine(fixture.Root, "host-launch.json"), JsonSerializer.SerializeToUtf8Bytes(identity)));

    private static async Task Stopped(OwnedWorkerJob job)
    {
        for (var count = 0; count < 250 && job.GetAccounting().ActiveProcesses != 0; count++) await Task.Delay(20);
        Assert.Equal(0u, job.GetAccounting().ActiveProcesses);
        Assert.Empty(job.ActiveProcessIds());
    }

    private static byte[] Quiescent(string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[checked((int)source.Length)];
        source.ReadExactly(bytes);
        return bytes;
    }

    private static string[] Fingerprints(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !Path.GetRelativePath(root, path).StartsWith("diagnostics" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed record Fixture(string Root, RuntimeInstance Instance, ContractReader Contracts, StateDatabase Database, string Target, Guid JournalId);
}
