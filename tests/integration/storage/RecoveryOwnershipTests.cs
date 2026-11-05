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
        Assert.Equal(new WriterStopObservation(true, WriterState.Stopped), new JournalWriterStore(fixture.Open()).Observe(fixture.JournalId));
        Assert.False(Directory.Exists(Path.Combine(fixture.Instance.RuntimeRoot, "journals")));
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
        var database = fixture.Open();
        var registered = Assert.Single(new RecoveryJournalRegistry(database).ReadSnapshot().Journals);
        Assert.Equal(JournalPhase.Committed, registered.Phase);
        Assert.All(new FileEffectOutbox(database).Read(fixture.JournalId), effect => Assert.Equal(FileEffectState.NotExecuted, effect.State));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(fixture.Target));
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var roots = fixture.PayloadRoots;
        var index = new StartupJournalRecovery(fixture.Instance, new(), database, roots).Recover(lease);
        Assert.True(index.Current.IsReady);
        Assert.Empty(index.Current.Isolations);
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
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        var error = Assert.Throws<IOException>(() => fixture.Open(admission: true));
        Assert.Equal("startup_journal_writer_stop_unconfirmed", error.Message);
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        await fixture.StopFamily();
    }

    [Fact(DisplayName = "A Stopped Creator With An Actually Live Registered Job Cannot Authorize Recovery")]
    public async Task RejectsLivingJobs()
    {
        using var fixture = await Fixture.Create("job");
        using var retained = OpenJobObjectW(4, false, fixture.Report.GetProperty("jobName").GetString()!);
        Assert.False(retained.IsInvalid);
        using var current = Process.GetCurrentProcess();
        Assert.Equal(WriterState.Unknown, WriterStopEvidence.ObserveJob(fixture.Report.GetProperty("jobName").GetString()!, current.SessionId + 1));
        await fixture.StopRoot();
        Assert.Equal(new WriterStopObservation(true, WriterState.Running), new JournalWriterStore(fixture.Open()).Observe(fixture.JournalId));
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        var error = Assert.Throws<IOException>(() => fixture.Open(admission: true));
        Assert.Equal("startup_journal_writer_stop_unconfirmed", error.Message);
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        await fixture.StopFamily();
        Assert.Equal(new WriterStopObservation(true, WriterState.Stopped), new JournalWriterStore(fixture.Open()).Observe(fixture.JournalId));
        output.WriteLine(JsonSerializer.Serialize(new { creatorExited = true, liveJobBlockedRecovery = true,
            job = fixture.Report.GetProperty("jobName").GetString(), completeFamilyStopped = true }));
    }

    [Fact(DisplayName = "A Disappeared Registered Job Name Cannot Stand In For Positive Writer Stop Evidence")]
    public async Task RejectsMissingJobs()
    {
        using var fixture = await Fixture.Create("job");
        await fixture.StopRoot();
        await fixture.StopFamily();
        var observed = new JournalWriterStore(fixture.Open()).Observe(fixture.JournalId);
        Assert.Equal(new WriterStopObservation(false, WriterState.Unknown), observed);
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.Equal("startup_journal_writer_stop_unconfirmed", Assert.Throws<IOException>(() => fixture.Open(admission: true)).Message);
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
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
        using (var connection = new SqliteConnection($"Data Source={fixture.Report.GetProperty("DatabasePath").GetString()};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = fault switch
            {
                "missing" => "DELETE FROM journal_writers;",
                "hash" => "UPDATE journal_writers SET image_sha256='zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz';",
                "path" => "UPDATE journal_writers SET image_path='relative.exe';",
                "sid" => "UPDATE journal_writers SET user_sid='not-a-sid';",
                _ => "UPDATE journal_writers SET writer_id='00000000-0000-0000-0000-000000000000';"
            };
            command.ExecuteNonQuery();
        }
        Assert.Equal(new WriterStopObservation(false, WriterState.Unknown), new JournalWriterStore(fixture.Open()).Observe(fixture.JournalId));
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.Equal("startup_journal_writer_stop_unconfirmed", Assert.Throws<IOException>(() => fixture.Open(admission: true)).Message);
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(fixture.Target));
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
        var registered = new RecoveryJournalRegistry(database).ReadSnapshot().Journals;
        Assert.Equal(new[] { JournalKind.ConfigExchange, JournalKind.ConfigEdit, JournalKind.FileCleanup,
            JournalKind.RunAttempt, JournalKind.UpdateApply, JournalKind.ExtensionApply }.Order(), registered.Select(item => item.Kind).Order());
        var writerStore = new JournalWriterStore(database);
        Assert.All(registered, item => Assert.Equal(new WriterStopObservation(true, WriterState.Stopped), writerStore.Observe(item.JournalId)));
        var before = SiteFingerprints(fixture.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var index = new StartupJournalRecovery(fixture.Instance, new(), database, fixture.PayloadRoots).Recover(lease);
        Assert.True(index.Current.IsReady);
        Assert.Empty(index.Current.Isolations);
        var completed = new RecoveryJournalRegistry(database).ReadSnapshot();
        Assert.All(completed.Journals, item => Assert.Equal(JournalPhase.Committed, item.Phase));
        Assert.All(completed.FileEffects, item => Assert.Equal(FileEffectState.NotExecuted, item.State));
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
        using var observation = new DatabaseObservation(database);
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.ThrowsAny<Exception>(() => fixture.Open(admission: true));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        Assert.ThrowsAny<Exception>(() => new StartupJournalRecovery(fixture.Instance, new(), database, fixture.PayloadRoots).Recover(lease));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        Assert.All(new RecoveryJournalRegistry(database).ReadSnapshot().Journals, item => Assert.Equal(JournalPhase.Prepared, item.Phase));
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
            _ = await OwnedCrashProcess.ReadReportAsync(report);
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
