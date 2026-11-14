using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static NexusPipeline.StorageTests.InitialRecoveryTests;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RecoveryOwnershipTests(ITestOutputHelper output)
{
    [Theory(DisplayName = "Actual Host Recovers An Accepted Missing Initial Journal Only After Its Recorded Creator Stops")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiresStoppedCreator(bool interruptRecoveryOwner)
    {
        using var fixture = await Fixture.Create("host");
        await fixture.StopRoot();
        if (interruptRecoveryOwner) await fixture.InterruptRecoveryOwner();
        var initialDb = fixture.Open();
        Assert.Equal(new WriterStopObservation(true, WriterState.Stopped), new JournalWriterStore(initialDb).Observe(fixture.JournalId));
        var accepted = new RecoveryJournalRegistry(initialDb).ReadOperationJournal(fixture.OperationId);
        Assert.Equal(fixture.JournalId, accepted.JournalId);
        Assert.Equal(JournalPhase.Prepared, accepted.Phase);
        var effects = new FileEffectOutbox(initialDb).Read(accepted.JournalId);
        Assert.Equal(FileEffectAction.InstallCandidate, Assert.Single(effects).Action);
        var journals = new JournalStore(fixture.Instance, new());
        Assert.ThrowsAny<IOException>(() => journals.Read(accepted.JournalId));
        var original = PhysicalObjectIdentity.Read(fixture.Target);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var host = new OwnedWorkerJob();
        var identity = host.Start(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!,
            ["serve", "--runtime-root", fixture.Instance.RuntimeRoot, "--port", port.ToString()], fixture.Root,
            fixture.Instance.InstanceId, Guid.NewGuid(), _ => { });
        using var process = Process.GetProcessById(identity.ProcessId);
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(250) };
        var served = false;
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10) && !process.HasExited)
        {
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/control/v1/capabilities");
                response.EnsureSuccessStatusCode();
                using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
                Assert.Equal(fixture.Instance.InstanceId, body.RootElement.GetProperty("instanceId").GetGuid());
                served = true;
                break;
            }
            catch (HttpRequestException) { await Task.Delay(25); }
            catch (TaskCanceledException) { await Task.Delay(25); }
        }
        Assert.True(served);
        Assert.True(await host.StopAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0u, host.GetAccounting().ActiveProcesses);
        var database = fixture.Open();
        var registered = AssertRegistration(new(database), accepted, JournalPhase.Committed);
        new JournalRecoveryEvidence(fixture.Instance, journals).VerifyAll([registered]);
        AssertEffectsClosed(effects, new FileEffectOutbox(database).Read(registered.JournalId));
        Assert.Equal(original, PhysicalObjectIdentity.Read(fixture.Target));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(fixture.Target));
        var history = journals.ReadAllRevisions(registered.JournalId).Select(item => item.Export()).ToArray();
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var roots = fixture.PayloadRoots;
        var index = new StartupJournalRecovery(fixture.Instance, new(), database, roots).Recover(lease);
        Assert.True(index.Current.IsReady);
        Assert.Empty(index.Current.Isolations);
        Assert.Equal(history, journals.ReadAllRevisions(registered.JournalId).Select(item => item.Export()));
        output.WriteLine(JsonSerializer.Serialize(new { identity, served, registered.Phase, preservedTarget = fixture.Target,
            repeatedStartup = "ready", interruptRecoveryOwner, evidence = fixture.Report,
            scope = "Actual Host startup with a killed creator and no initial physical journal" }));
    }

    [Fact(DisplayName = "A Recorded Live Creator Cannot Be Declared Stopped By Acquiring A Separate Database Object")]
    public async Task RejectsLivingCreator()
    {
        using var fixture = await Fixture.Create("host");
        var database = fixture.Open();
        Assert.Equal(new WriterStopObservation(true, WriterState.Running), new JournalWriterStore(database).Observe(fixture.JournalId));
        AssertReadOnlyRefusal(fixture, database);
        await fixture.StopFamily();
    }

    [Fact(DisplayName = "A Stopped Creator With An Actually Live Registered Job Cannot Authorize Recovery")]
    public async Task RejectsLivingJobs()
    {
        using var fixture = await Fixture.Create("job");
        var name = fixture.Report.GetProperty("jobName").GetString()!;
        using var retained = OpenJobObjectW(4, false, name);
        Assert.False(retained.IsInvalid);
        using var current = Process.GetCurrentProcess();
        Assert.Equal(WriterState.Unknown, WriterStopEvidence.ObserveJob(name, current.SessionId + 1));
        await fixture.StopRoot();
        Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveJob(name, current.SessionId));
        var database = fixture.Open();
        Assert.Equal(new WriterStopObservation(true, WriterState.Running), new JournalWriterStore(database).Observe(fixture.JournalId));
        AssertReadOnlyRefusal(fixture, database);
        await fixture.StopFamily();
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveJob(name, current.SessionId));
        Assert.Equal(new WriterStopObservation(true, WriterState.Stopped), new JournalWriterStore(fixture.Open()).Observe(fixture.JournalId));
        output.WriteLine(JsonSerializer.Serialize(new { creatorExited = true, liveJobBlockedRecovery = true,
            job = fixture.Report.GetProperty("jobName").GetString(), completeFamilyStopped = true }));
    }

    [Fact(DisplayName = "A Disappeared Registered Job Name Cannot Stand In For Positive Writer Stop Evidence")]
    public async Task RejectsMissingJobs()
    {
        using var fixture = await Fixture.Create("job");
        var database = fixture.Open();
        Assert.Equal(new WriterStopObservation(true, WriterState.Running), new JournalWriterStore(database).Observe(fixture.JournalId));
        await fixture.StopFamily();
        var observed = new JournalWriterStore(database).Observe(fixture.JournalId);
        Assert.Equal(new WriterStopObservation(false, WriterState.Unknown), observed);
        AssertReadOnlyRefusal(fixture, database);
    }

    [Theory(DisplayName = "Missing Writer Evidence Is Rejected Read Only Instead Of Assuming A Stopped Creator")]
    [InlineData("missing")]
    [InlineData("hash")]
    [InlineData("path")]
    [InlineData("sid")]
    [InlineData("writer-id")]
    public async Task RequiresWriterEvidence(string fault)
    {
        using var fixture = await Fixture.Create("host");
        await fixture.StopRoot();
        using (var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open($"Data Source={fixture.Report.GetProperty("DatabasePath").GetString()};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            var change = fault switch
            {
                "missing" => "DELETE FROM main.journal_writers",
                "hash" => "UPDATE main.journal_writers SET image_sha256='zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz'",
                "path" => "UPDATE main.journal_writers SET image_path='relative.exe'",
                "sid" => "UPDATE main.journal_writers SET user_sid='not-a-sid'",
                _ => "UPDATE main.journal_writers SET writer_id='00000000-0000-0000-0000-000000000000'"
            };
            command.CommandText = change + " WHERE journal_id=$journal;";
            command.Parameters.AddWithValue("$journal", fixture.JournalId.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var database = fixture.Open();
        Assert.Equal(new WriterStopObservation(false, WriterState.Unknown), new JournalWriterStore(database).Observe(fixture.JournalId));
        AssertReadOnlyRefusal(fixture, database);
    }

    [Fact(DisplayName = "Recovery Cannot Use An Expired Exclusive Instance Lease")]
    public async Task RejectsExpiredLease()
    {
        using var fixture = await Fixture.Create("host");
        await fixture.StopRoot();
        var database = fixture.Open();
        var lease = InstanceLease.Acquire(fixture.Instance, new());
        lease.Dispose();
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        var recovery = new StartupJournalRecovery(fixture.Instance, new(), database, fixture.PayloadRoots);
        Assert.Equal("runtime_exclusive_lease_required", Assert.Throws<IOException>(() => recovery.Recover(lease)).Message);
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
    }

    [Fact(DisplayName = "Process Stop Evidence Distinguishes The Current Kernel Identity From A Reused Pid")]
    public void BindsKernelIdentity()
    {
        var current = ProcessIdentityReader.Read(Environment.ProcessId);
        Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(current));
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(current with { CreatedAtUtc = current.CreatedAtUtc.AddTicks(1) }));
        using var process = Process.GetCurrentProcess();
        Assert.Equal(WriterState.Unknown, WriterStopEvidence.ObserveJob("Local\\foreign-job", process.SessionId));
    }

    [Fact(DisplayName = "Product Startup Recovery Closes All Six Real Prepared Kinds Using Their Recorded Stopped Creator")]
    public async Task RecoversRecordedCreators()
    {
        using var fixture = await Fixture.Create("six");
        await fixture.StopRoot();
        var database = fixture.Open(admission: true);
        var registry = new RecoveryJournalRegistry(database);
        var accepted = registry.ReadSnapshot();
        Assert.Equal(new[] { JournalKind.ConfigExchange, JournalKind.ConfigEdit, JournalKind.FileCleanup,
            JournalKind.RunAttempt, JournalKind.UpdateApply, JournalKind.ExtensionApply }.Order(), accepted.Journals.Select(item => item.Kind).Order());
        var writerStore = new JournalWriterStore(database);
        Assert.All(accepted.Journals, item => Assert.Equal(new WriterStopObservation(true, WriterState.Stopped), writerStore.Observe(item.JournalId)));
        var before = SiteFingerprints(fixture.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var recovery = new StartupJournalRecovery(fixture.Instance, new(), database, fixture.PayloadRoots);
        var index = recovery.Recover(lease);
        Assert.True(index.Current.IsReady);
        Assert.Empty(index.Current.Isolations);
        var completed = registry.ReadSnapshot();
        var bound = accepted.Journals.Select(item => AssertRegistration(registry, item, JournalPhase.Committed)).ToArray();
        Assert.Equal(bound.Select(item => item.JournalId).Order(), completed.Journals.Select(item => item.JournalId).Order());
        AssertEffectsClosed(accepted.FileEffects, completed.FileEffects);
        var receipts = new AttemptReceiptStore(database);
        var receipt = receipts.Read(fixture.AttemptId);
        Assert.NotNull(receipt);
        Assert.Equal(fixture.OperationId, receipt.OperationId); Assert.Equal(fixture.JournalId, receipt.JournalId);
        Assert.Equal(fixture.RunId, receipt.Facts.RunId); Assert.Equal(fixture.AttemptId, receipt.Facts.AttemptId);
        Assert.Equal(EngineOutcome.NotStarted, receipt.Facts.Engine);
        Assert.Equal(RecoveryOutcome.NotRequired, receipt.Facts.Recovery);
        var journals = new JournalStore(fixture.Instance, new());
        new JournalRecoveryEvidence(fixture.Instance, journals).VerifyAll(bound);
        var histories = bound.ToDictionary(item => item.JournalId, item => journals.ReadAllRevisions(item.JournalId).Select(revision => revision.Export()).ToArray());
        var repeated = recovery.Recover(lease);
        Assert.True(repeated.Current.IsReady); Assert.Empty(repeated.Current.Isolations);
        foreach (var journal in bound)
            Assert.Equal(histories[journal.JournalId], journals.ReadAllRevisions(journal.JournalId).Select(item => item.Export()));
        Assert.Equal(completed.FileEffects, registry.ReadSnapshot().FileEffects);
        Assert.Equal(receipt, receipts.Read(fixture.AttemptId));
        Assert.Equal(before, SiteFingerprints(fixture.Instance.RuntimeRoot));
        output.WriteLine(JsonSerializer.Serialize(new { input = fixture.Report, completedKinds = completed.Journals.Select(item => item.Kind.ToString()),
            indexReady = index.Current.IsReady, unknownWriterAssumptions = false, retainedManagedSite = before }));
    }

    [Fact(DisplayName = "A Late Unknown Sixth Plan Prevents Product Startup From Recording Any Earlier Recovery Write")]
    public async Task RejectsUnknownSuffix()
    {
        using var fixture = await Fixture.Create("six-unknown");
        await fixture.StopRoot();
        var database = fixture.Open();
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var registry = new RecoveryJournalRegistry(database);
        var accepted = registry.ReadSnapshot();
        var plan = Assert.Single(accepted.Journals, item => item.Kind == JournalKind.ExtensionApply);
        Assert.Equal(plan.JournalId, accepted.Journals[^1].JournalId);
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.Equal("unsupported_or_invalid_contract", Assert.Throws<JsonException>(() => fixture.Open(admission: true)).Message);
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        using var observation = new DatabaseObservation(database);
        var beforeRecovery = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.Equal("unsupported_or_invalid_contract", Assert.Throws<JsonException>(() =>
            new StartupJournalRecovery(fixture.Instance, new(), database, fixture.PayloadRoots).Recover(lease)).Message);
        Assert.Equal(beforeRecovery, Fingerprints(fixture.Instance.RuntimeRoot));
        foreach (var item in accepted.Journals)
            Assert.Equal(item.Revision, AssertRegistration(registry, item, JournalPhase.Prepared).Revision);
        Assert.Equal(accepted.FileEffects, registry.ReadSnapshot().FileEffects);
        Assert.Null(new AttemptReceiptStore(database).Read(fixture.AttemptId));
    }

    private static void AssertReadOnlyRefusal(Fixture fixture, StateDatabase database)
    {
        var registry = new RecoveryJournalRegistry(database);
        var accepted = registry.ReadOperationJournal(fixture.OperationId);
        Assert.Equal(fixture.JournalId, accepted.JournalId);
        var original = PhysicalObjectIdentity.Read(fixture.Target);
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.Equal("startup_journal_writer_stop_unconfirmed", Assert.Throws<IOException>(() => fixture.Open(admission: true)).Message);
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        Assert.Equal(accepted.Revision, AssertRegistration(registry, accepted, JournalPhase.Prepared).Revision);
        var isolation = Assert.Single(registry.ReadSnapshot().Isolations);
        Assert.Equal(accepted.JournalId, isolation.JournalId);
        Assert.Equal(Assert.Single(accepted.ResourceKeys), isolation.ResourceKey);
        Assert.Equal(original, PhysicalObjectIdentity.Read(fixture.Target));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(fixture.Target));
    }

    private static void AssertEffectsClosed(IReadOnlyList<FileEffect> accepted, IReadOnlyList<FileEffect> completed)
    {
        Assert.Equal(accepted.Count, completed.Count);
        foreach (var effect in accepted)
        {
            var closed = Assert.Single(completed, item => item.JournalId == effect.JournalId && item.StepId == effect.StepId);
            Assert.NotNull(closed.ReceiptDigest);
            Assert.Equal(effect with { State = FileEffectState.NotExecuted, ReceiptDigest = closed.ReceiptDigest }, closed);
        }
    }

    private static string[] SiteFingerprints(string runtimeRoot)
    {
        var root = Path.GetDirectoryName(runtimeRoot)!;
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).StartsWith("runtime" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    }

    private static string[] Fingerprints(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "instance.lock")
        .Where(path => !Path.GetRelativePath(root, path).StartsWith("diagnostics" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Fingerprint(path)).ToArray();

    private static string Fingerprint(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJobObjectW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    private sealed class Fixture : IDisposable
    {
        private readonly OwnedWorkerJob _owner = new();
        private Process? _rootProcess;
        internal required string Root { get; init; }
        internal JsonElement Report { get; private set; }
        internal RuntimeInstance Instance => new InstanceRoot(new()).OpenExisting(Report.GetProperty("runtimeRoot").GetString()!);
        internal Guid JournalId => Report.GetProperty("journalId").GetGuid();
        internal Guid OperationId => Report.GetProperty("operationId").GetGuid();
        internal Guid AttemptId => Report.GetProperty("attemptId").GetGuid();
        internal Guid RunId => Report.GetProperty("runId").GetGuid();
        internal string Target => Report.GetProperty("target").GetString()!;
        internal PayloadApplyRoots PayloadRoots => Report.TryGetProperty("hostPayloadRoot", out var host)
            ? new(host.GetString()!, Report.GetProperty("extensionPayloadRoot").GetString()!)
            : new(Path.Combine(Root, "payload"), Path.Combine(Instance.RuntimeRoot, "extensions"));

        internal StateDatabase Open(bool admission = false) => StateDatabase.OpenExisting(Instance,
            Path.Combine(Instance.RuntimeRoot, "diagnostics", Guid.NewGuid().ToString("N")),
            admission ? new JournalStartupAdmission(Instance, new(), PayloadRoots) : null);

        internal static async Task<Fixture> Create(string mode)
        {
            var fixture = new Fixture { Root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N")) };
            Directory.CreateDirectory(fixture.Root);
            var report = Path.Combine(fixture.Root, "accepted.json");
            try
            {
                var identity = fixture._owner.Start(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!,
                    ["startup-journal", fixture.Root, mode, report], fixture.Root, Guid.NewGuid(), Guid.NewGuid(), _ => { });
                fixture._rootProcess = Process.GetProcessById(identity.ProcessId);
                fixture.Report = await OwnedCrashProcess.ReadReportAsync(report);
                Assert.Equal("faultReady", fixture.Report.GetProperty("status").GetString());
                Assert.Equal(identity.ProcessId, fixture.Report.GetProperty("processId").GetInt32());
                Assert.Equal(fixture.Instance.InstanceId, fixture.Report.GetProperty("instanceId").GetGuid());
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        internal async Task StopRoot()
        {
            _rootProcess!.Kill(entireProcessTree: false);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _rootProcess.WaitForExitAsync(deadline.Token);
        }

        internal async Task StopFamily() => Assert.True(await _owner.StopAsync(TimeSpan.FromSeconds(5)));

        internal async Task InterruptRecoveryOwner()
        {
            using var owner = new OwnedWorkerJob();
            var report = Path.Combine(Root, "recovery-owner.json");
            var identity = owner.Start(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!,
                ["startup-recovery-owner", Instance.RuntimeRoot, report], Root, Instance.InstanceId, Guid.NewGuid(), _ => { });
            using var process = Process.GetProcessById(identity.ProcessId);
            var ready = await OwnedCrashProcess.ReadReportAsync(report);
            Assert.Equal("faultReady", ready.GetProperty("status").GetString());
            Assert.Equal(identity.ProcessId, ready.GetProperty("processId").GetInt32());
            Assert.Equal(new WriterStopObservation(true, WriterState.Running), new JournalWriterStore(Open()).Observe(JournalId));
            process.Kill(entireProcessTree: false);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(await owner.StopAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(new WriterStopObservation(true, WriterState.Stopped), new JournalWriterStore(Open()).Observe(JournalId));
        }

        public void Dispose() { _owner.Dispose(); _rootProcess?.Dispose(); }
    }
}
