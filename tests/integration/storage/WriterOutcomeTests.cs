using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class WriterOutcomeTests
{
    [Fact(DisplayName = "A Failure After Root Publication Stops The Real Family Without Issuing A Completed Closure Or Restarting")]
    public async Task StopsFailedPublication()
    {
        using var fixture = new NativeWriterFixture(failAfterRootPublication: true);
        Assert.Equal(1, fixture.Scope.PreparedCalls); Assert.Equal(1, fixture.Scope.RegisterCalls);
        Assert.Equal(0U, fixture.Scope.Job.GetAccounting().TotalProcesses);
        Assert.Equal("fixture_root_published", Assert.Throws<IOException>(() => fixture.Writer.Start()).Message);
        var root = Assert.IsType<ProcessIdentity>(fixture.Scope.Root);
        var command = Assert.IsType<ConfigurationEditorCommand>(fixture.Scope.Command);
        Assert.Equal(1, fixture.Observer.Calls); Assert.Equal(1, fixture.Scope.RootCalls);
        Assert.Equal(command.ExecutablePath, root.ImagePath);
        Assert.Equal(command.ExecutableSha256, root.ImageSha256);
        Assert.Contains(root, fixture.Observer.Family);
        Assert.False(File.Exists(fixture.Report)); Assert.Equal("original", File.ReadAllText(fixture.Target));
        StoppedConfigurationEditor? stopped = null;
        var error = await Assert.ThrowsAsync<IOException>(async () =>
        {
            stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        });
        Assert.Equal("configuration_writer_start_outcome_unknown", error.Message);
        Assert.Null(stopped); Assert.Equal(0, fixture.Scope.BindCalls); Assert.Equal(0, fixture.Scope.ClosedCalls);
        Assert.Null(fixture.Scope.Closed); Assert.Null(fixture.Scope.Reference);
        Assert.Equal(root, fixture.Scope.Root); Assert.Equal(0L, fixture.Scope.ClosedElapsedMilliseconds);
        fixture.AssertStoppedFamily(fixture.Observer.Family);
        Assert.Equal("configuration_writer_start_outcome_unknown", (await Assert.ThrowsAsync<IOException>(
            () => fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)))).Message);
        Assert.Equal("configuration_editor_start_closed", Assert.Throws<IOException>(() => fixture.Writer.Start()).Message);
        Assert.Equal("configuration_writer_not_started", Assert.Throws<IOException>(() => fixture.Writer.ElapsedSinceStart).Message);
        Assert.Equal(1, fixture.Scope.CloseCalls); Assert.Equal(1, fixture.Scope.StartCalls);
        Assert.Equal(0, fixture.Scope.ClosedCalls); Assert.False(File.Exists(fixture.Report));
    }

    [Fact(DisplayName = "A Resumed Native Writer Produces Its Real Output And One Verified Closure With A Frozen Elapsed Clock")]
    public async Task RecordsVerifiedClosure()
    {
        using var fixture = new NativeWriterFixture(failAfterRootPublication: false);
        Assert.Equal(1, fixture.Scope.PreparedCalls); Assert.Equal(1, fixture.Scope.RegisterCalls);
        Assert.Equal(0U, fixture.Scope.Job.GetAccounting().TotalProcesses);
        var started = fixture.Writer.Start();
        Assert.Equal(fixture.Scope.ProcessScopeId, started.AttemptId);
        var report = await OwnedCrashProcess.ReadReportAsync(fixture.Report);
        Assert.Equal(started.ProcessId, report.GetProperty("processId").GetInt32());
        Assert.Equal("edited", File.ReadAllText(fixture.Target));
        Assert.Equal(1, fixture.Observer.Calls); Assert.Equal(1, fixture.Scope.RootCalls);
        var family = fixture.Scope.CaptureFamily();
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        Assert.True(stopped.WasExecuted);
        var closure = Assert.IsType<FrozenJobClosure>(fixture.Scope.Closed);
        Assert.True(closure.WasExecuted); Assert.Equal(fixture.Scope.Root, closure.Root);
        Assert.Contains(closure.Root!, family); Assert.Equal(closure.Family.Length, family.Length);
        Assert.All(family, member => Assert.Contains(member, closure.Family));
        Assert.Null(closure.RunElapsedMilliseconds);
        Assert.Equal(1, fixture.Scope.BindCalls); Assert.Equal(1, fixture.Scope.ClosedCalls);
        Assert.Equal(1, fixture.Scope.CloseCalls); Assert.True(fixture.Scope.ClosedElapsedMilliseconds >= 0);
        Assert.Equal(fixture.Scope.Reference, stopped.Reference);
        var saved = fixture.Scope.ReadClosure();
        Assert.Equal(closure.Root, saved.Root); Assert.True(saved.WasExecuted);
        Assert.True(closure.Family.SequenceEqual(saved.Family));
        var elapsed = fixture.Writer.ElapsedSinceStart;
        Assert.Equal(fixture.Scope.ClosedElapsedMilliseconds, elapsed.Ticks / TimeSpan.TicksPerMillisecond);
        fixture.AssertStoppedFamily(family);
        Assert.Same(stopped, await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(elapsed, fixture.Writer.ElapsedSinceStart);
        Assert.Equal("configuration_editor_start_closed", Assert.Throws<IOException>(() => fixture.Writer.Start()).Message);
        Assert.Equal(1, fixture.Scope.ClosedCalls);
    }

    private sealed class NativeWriterFixture : IDisposable
    {
        internal string Target { get; }
        internal string Report { get; }
        internal RecordingScope Scope { get; }
        internal RootPublicationObserver Observer { get; }
        internal ConfigurationWriterProcess Writer { get; }
        private readonly InstanceLease _lease;

        internal NativeWriterFixture(bool failAfterRootPublication)
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "writer-start-outcome-" + Guid.NewGuid().ToString("N"));
            var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
            _lease = InstanceLease.Acquire(instance, new());
            try
            {
                Target = Path.Combine(root, "target.json"); Report = Path.Combine(root, "ready.json");
                var input = Path.Combine(root, "input.json"); File.WriteAllText(Target, "original");
                File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { operations = new[] {
                    new { target = Target, kind = "file", value = "edited" } } }));
                var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
                var command = new ConfigurationEditorCommand(executable, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable))),
                    ["configuration-editor-writer", input, Report], root);
                Scope = new(instance, command); Observer = new(Scope, failAfterRootPublication);
                Writer = new(instance, _lease, Scope, Observer);
            }
            catch { _lease.Dispose(); throw; }
        }

        internal void AssertStoppedFamily(IReadOnlyList<ProcessIdentity> family)
        {
            Assert.NotEmpty(family);
            Assert.Equal(family.Count, family.DistinctBy(member => (member.ProcessId, member.CreatedAtUtc)).Count());
            var accounting = Scope.Job.GetAccounting();
            Assert.Equal((uint)family.Count, accounting.TotalProcesses); Assert.Equal(0U, accounting.ActiveProcesses);
            Assert.Empty(Scope.Job.ActiveProcessIds());
            Assert.All(family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        }

        public void Dispose()
        {
            try { Writer.Dispose(); }
            finally { _lease.Dispose(); }
        }
    }

    // This scope isolates the shared native owner; it makes no SQL admission or whole-attempt claim.
    private sealed class RecordingScope(RuntimeInstance instance, ConfigurationEditorCommand command) : IConfigurationWriterScope
    {
        public Guid OperationId { get; } = Guid.NewGuid();
        public Guid ProcessScopeId { get; } = Guid.NewGuid();
        public ImmutableArray<Guid> JournalIds { get; } = [Guid.NewGuid()];
        public ConfigurationEditorCommand? Command { get; } = command;
        internal OwnedWorkerJob Job { get; private set; } = null!;
        internal ProcessIdentity? Root { get; private set; }
        internal int PreparedCalls { get; private set; }
        internal int StartCalls { get; private set; }
        internal int RegisterCalls { get; private set; }
        internal int RootCalls { get; private set; }
        internal int CloseCalls { get; private set; }
        internal int BindCalls { get; private set; }
        internal int ClosedCalls { get; private set; }
        internal long? ClosedElapsedMilliseconds { get; private set; }
        internal FrozenJobClosure? Closed { get; private set; }
        internal BlobReference? Reference { get; private set; }
        private JobClosureCodec Codec => new(instance, new(), new(instance, new()));

        public void RequirePrepared() => PreparedCalls++;
        public void RequireStart() => StartCalls++;
        public void RegisterJob(OwnedWorkerJob job) { RegisterCalls++; Job = job; }
        public void RecordRoot(string jobName, ProcessIdentity root)
        {
            Assert.Equal(Job.Name, jobName); RootCalls++; Root = root;
        }
        public void CloseAdmission(long elapsedMilliseconds) { CloseCalls++; ClosedElapsedMilliseconds = elapsedMilliseconds; }
        public FrozenJobClosure BindClosure(FrozenJobClosure closure, long elapsedMilliseconds)
        {
            Assert.Equal(ClosedElapsedMilliseconds, elapsedMilliseconds); BindCalls++; return closure;
        }
        public BlobReference RecordClosed(FrozenJobClosure closure)
        {
            ClosedCalls++; Closed = closure; return Reference = Codec.Write(closure);
        }
        internal ProcessIdentity[] CaptureFamily() => Job.ActiveProcessIds().Select(ProcessIdentityReader.Read).ToArray();
        internal FrozenJobClosure ReadClosure() => Codec.Read(Assert.IsType<BlobReference>(Reference));
    }

    private sealed class RootPublicationObserver(RecordingScope scope, bool fail) : IWriterPersistenceObserver
    {
        internal int Calls { get; private set; }
        internal ProcessIdentity[] Family { get; private set; } = [];
        public void Reached(WriterPersistencePoint point, ProcessIdentity root)
        {
            Assert.Equal(WriterPersistencePoint.RootPublished, point);
            Assert.Equal(scope.Root, root); Calls++; Family = scope.CaptureFamily();
            Assert.Contains(root, Family);
            if (fail) throw new IOException("fixture_root_published");
        }
    }
}
