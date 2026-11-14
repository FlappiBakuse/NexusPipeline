using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RunRecoveryTests
{
    private const string Raw = "{\"tasks\":{\"one\":true,\"new\":false},\"count\":41}";
    private const string Normalized = "{\"tasks\":{\"one\":false,\"new\":false},\"count\":41}";
    private const string Initial = "{\"tasks\":{\"one\":false},\"count\":17}";

    [Theory(DisplayName = "A Real Run Owner Loss After Sealing Restoration Or Before Job Creation Completes Only Its Verified Interrupted Group")]
    [InlineData("sealed")]
    [InlineData("restored")]
    [InlineData("no-job")]
    public async Task CompletesInterruptedGroups(string point)
    {
        var fixture = new ConfigurationRunFixture(additional: true, nativeWriter: true, childWriter: true);
        // The child owns scene restoration; failed assertions must preserve its scene while releasing the parent's read leases.
        using var execution = fixture.Execution;
        using var seedLease = fixture.Lease;
        var originals = CaptureOriginals(fixture); var sessionId = Guid.NewGuid();
        var (parent, input, report) = PrepareInput(fixture, point, sessionId);
        fixture.Lease.Dispose();
        using var owner = new OwnedCrashProcess();
        var identity = owner.Start(["configuration-run-recovery", input, report], fixture.Source.Root, fixture.Source.Instance.InstanceId,
            sessionId, Path.Combine(fixture.Source.Root, "run-recovery-owner-process.json"));
        var fault = await WaitBoundaryAsync(report);
        Assert.True(fault.GetProperty("status").GetString() == "faultReady", fault.GetRawText());
        Assert.Equal(identity.ProcessId, fault.GetProperty("processId").GetInt32()); Assert.Equal(point, fault.GetProperty("point").GetString());
        Assert.Equal(sessionId, fault.GetProperty("sessionId").GetGuid());
        var stopped = await owner.StopAsync();
        Assert.True(stopped.CompleteJobStopped); Assert.Equal(0u, stopped.Accounting.ActiveProcesses);
        Assert.All(stopped.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        File.WriteAllBytes(Path.Combine(fixture.Source.Root, "run-recovery-owner-stopped.json"), JsonSerializer.SerializeToUtf8Bytes(stopped));
        var instance = new InstanceRoot(new()).OpenExisting(fixture.Source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(fixture.Source.Root, "run-restart-diagnostics"), new JournalStartupAdmission(instance, new()));
        var store = new ConfigurationRunStore(instance, database, new()); var pending = store.Read(sessionId, ConfigurationRunFixture.Subject)!;
        AssertExactChildren(parent, fault, pending, instance);
        var raw = AssertNativeEvidence(fixture, instance, database, pending, fault, identity, point);
        VerifyOriginals(originals, fixture, pending, backup: point == "sealed"); AssertSaved(fixture, 1, Initial);
        var index = new StartupJournalRecovery(instance, new(), database, new(fixture.Source.Root, Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
        fixture.VerifyOriginal();
        var journals = new RecoveryJournalRegistry(database).ReadSnapshot().Journals;
        Assert.All(pending.Resources, resource => Assert.Equal(JournalPhase.Committed, Assert.Single(journals, item => item.JournalId == resource.JournalId).Phase));
        Assert.Equal(JournalPhase.Prepared, Assert.Single(journals, item => item.JournalId == Guid.Parse(parent.JournalId)).Phase);
        var isolation = index.Current.Isolations.ToArray(); Assert.NotEmpty(isolation);
        Assert.All(isolation, item => Assert.Equal(Guid.Parse(parent.JournalId), item.JournalId));
        Assert.NotEmpty(index.Current.ResourceHolds); Assert.All(index.Current.ResourceHolds, item => Assert.Equal(fixture.Owner, item.OperationId));
        Assert.Empty(index.Current.SnapshotCompletions);
        Assert.Null(index.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, fixture.Owner,
            pending.Resources.Select(resource => ConfigurationRevisionService.ResourceKey(resource.Address)).ToHashSet(StringComparer.Ordinal)));
        var manager = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        var completion = new RunCompletionService(instance, database, new(), lease, manager, index); var operation = Guid.NewGuid();
        var result = completion.CompleteInterrupted(operation, ConfigurationRunFixture.Subject, sessionId);
        Assert.True(result.IsNew); Assert.Equal(ConfigurationRunPhase.Interrupted, result.Session.Phase);
        Assert.Equal(point == "no-job" ? 0 : pending.ClosedElapsedMilliseconds, result.Session.ClosedElapsedMilliseconds);
        AssertSaved(fixture, point == "no-job" ? 1 : 2, point == "no-job" ? Initial : Normalized);
        Assert.All(result.Items, item => Assert.Equal(point == "no-job" ? "configuration_sync_not_due" : "configuration_sync_allowed", item.ReasonCode));
        fixture.VerifyOriginal(); Assert.Empty(index.Current.ResourceHolds); Assert.Equal(isolation, index.Current.Isolations.ToArray());
        Assert.Empty(index.Current.SnapshotCompletions);
        var intent = Assert.Single(SnapshotIntentCodec.Read(new JournalStore(instance, new()).Read(Guid.Parse(parent.JournalId)).Root));
        Assert.Equal(operation, intent.CompletionOperationId); Assert.Equal(sessionId, intent.SessionId);
        var counts = new[] { fixture.Count("operations"), fixture.Count("journal_writer_jobs"), fixture.Count("journal_writer_job_roots"), fixture.Count("journal_writer_job_closures") };
        Assert.False(completion.CompleteInterrupted(operation, ConfigurationRunFixture.Subject, sessionId).IsNew);
        Assert.Equal(counts, new[] { fixture.Count("operations"), fixture.Count("journal_writer_jobs"), fixture.Count("journal_writer_job_roots"), fixture.Count("journal_writer_job_closures") });
        ConfigurationRunInventory.VerifyReadOnly(instance, database, new());
        if (raw is not null) AssertRawRetained(instance, database, raw);
    }

    private static (RunAttemptPlan Parent, string Input, string Report) PrepareInput(ConfigurationRunFixture fixture, string point, Guid sessionId)
    {
        RunAttemptPlan parent;
        using (var root = fixture.Acquire(fixture.Request))
        using (var prepared = fixture.Preparation.Prepare(root, fixture.Owner, fixture.Execution.Plan, fixture.StepId, fixture.Request)) parent = fixture.Parent(prepared);
        var input = Path.Combine(fixture.Source.Root, "run-recovery-input.json"); var report = input + ".fault";
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { runtimeRoot = fixture.Source.Instance.RuntimeRoot, request = fixture.Request,
            parent, sessionId, subject = ConfigurationRunFixture.Subject, point, nativeReport = fixture.NativeReport }));
        return (parent, input, report);
    }

    private static void AssertExactChildren(RunAttemptPlan parent, JsonElement report, ConfigurationRunStatus session, RuntimeInstance instance)
    {
        var expected = parent.ConfigurationPlans.Select(item => new BlobReference(item.Plan.RelativePath, item.Plan.SizeBytes, item.Plan.Sha256))
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, report.GetProperty("children").Deserialize<BlobReference[]>()!);
        var actual = session.Resources.Select(item => new BlobReference(item.PlanPath, new FileInfo(Path.Combine(instance.RuntimeRoot, item.PlanPath)).Length, item.PlanDigest))
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }

    private static BlobReference? AssertNativeEvidence(ConfigurationRunFixture fixture, RuntimeInstance instance, StateDatabase database,
        ConfigurationRunStatus session, JsonElement report, OwnedWorkerIdentity owner, string point)
    {
        var jobs = Jobs(database, session.SessionId);
        if (point == "no-job")
        {
            Assert.Empty(jobs); Assert.Null(session.ClosedElapsedMilliseconds); Assert.False(session.EnteredRunning); Assert.False(session.IsWriterAdmissionClosed);
            Assert.Equal(0, fixture.Count("journal_writer_job_roots")); Assert.Equal(0, fixture.Count("journal_writer_job_closures")); return null;
        }
        Assert.Equal(2, jobs.Length); Assert.Single(jobs.Select(item => (item.Writer, item.Name, item.DesktopSession)).Distinct());
        Assert.True(session.ClosedElapsedMilliseconds >= 15000); Assert.True(session.IsWriterAdmissionClosed); Assert.True(session.EnteredRunning);
        Assert.Equal(report.GetProperty("elapsedMilliseconds").GetInt64(), session.ClosedElapsedMilliseconds);
        var root = report.GetProperty("rootProcessId").GetInt32(); var child = report.GetProperty("childProcessId").GetInt32(); Assert.NotEqual(root, child);
        BlobReference? raw = null; FrozenJobClosure? common = null;
        foreach (var job in jobs)
        {
            Assert.Equal(WriterState.Unknown, WriterStopEvidence.ObserveJob(job.Name, job.DesktopSession));
            var closure = new JobClosureStore(instance, database, new()).Read(job.Journal, job.Writer, job.Name, job.DesktopSession)!;
            Assert.NotNull(closure); Assert.True(closure.WasExecuted); Assert.Equal(session.SessionId, closure.OperationId);
            Assert.Equal(session.ClosedElapsedMilliseconds, closure.RunElapsedMilliseconds);
            Assert.Equal(owner.ProcessId, closure.Creator.ProcessId); Assert.Equal(owner.CreatedAtUtc, closure.Creator.CreatedAtUtc);
            Assert.Equal(owner.ImageSha256, closure.Creator.ImageSha256); Assert.Equal(owner.UserSid, closure.Creator.UserSid);
            Assert.Equal(root, closure.Root!.ProcessId);
            Assert.Equal(closure.Family.Length, closure.Family.DistinctBy(member => (member.ProcessId, member.CreatedAtUtc)).Count());
            Assert.Single(closure.Family, member => member.ProcessId == root); Assert.Single(closure.Family, member => member.ProcessId == child);
            Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            if (common is not null)
            {
                Assert.Equal(common.Root, closure.Root); Assert.Equal(common.Family.ToArray(), closure.Family.ToArray());
                Assert.Equal(common.ClosedAt, closure.ClosedAt); Assert.Equal(common.JournalIds.ToArray(), closure.JournalIds.ToArray());
            }
            common = closure;
            var physical = new JournalStore(instance, new()).Read(job.Journal);
            Assert.Equal(point == "sealed" ? JournalPhase.Applied : JournalPhase.Committed, physical.Phase);
            Assert.Equal(2, physical.Root.GetProperty("participants").GetArrayLength());
            var binding = ConfigurationJournalBinding.Read(instance, physical, new(), database);
            var output = binding.Prepared!.Plan.Candidate.Snapshot; Assert.Equal(Raw, fixture.Read(output));
            var entry = output.Entries.Single(item => item.Kind == FileObjectKind.File);
            raw = new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!);
        }
        Assert.Equal(2, fixture.Count("journal_writer_job_roots")); Assert.Equal(2, fixture.Count("journal_writer_job_closures"));
        return raw;
    }

    private static (Guid Journal, Guid Writer, string Name, int DesktopSession)[] Jobs(StateDatabase database, Guid session)
    {
        using var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open(new SqliteConnectionStringBuilder { DataSource = database.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT b.journal_id,b.writer_id,b.job_name,b.session_id FROM main.journal_writer_jobs b JOIN configuration.configuration_run_resources r " +
            "ON r.journal_id=b.journal_id WHERE r.session_id=$session ORDER BY b.journal_id;";
        command.Parameters.AddWithValue("$session", session.ToString("D"));
        var result = new List<(Guid, Guid, string, int)>(); using var rows = command.ExecuteReader();
        while (rows.Read()) result.Add((Guid.Parse(rows.GetString(0)), Guid.Parse(rows.GetString(1)), rows.GetString(2), rows.GetInt32(3)));
        return result.ToArray();
    }

    private static Dictionary<string, (FileTreeSnapshot Tree, IReadOnlyList<PhysicalTreeObject> Objects)> CaptureOriginals(ConfigurationRunFixture fixture) =>
        fixture.Request.Resources.ToDictionary(item => item.Target.TargetPath, item =>
        {
            var tree = new FileTreeScanner(SnapshotBudget.Default).Capture(item.Target.TargetPath, default);
            return (tree, PhysicalTreeIdentity.Capture(item.Target.TargetPath, tree));
        });
    private static void VerifyOriginals(IReadOnlyDictionary<string, (FileTreeSnapshot Tree, IReadOnlyList<PhysicalTreeObject> Objects)> originals,
        ConfigurationRunFixture fixture, ConfigurationRunStatus session, bool backup)
    {
        foreach (var resource in fixture.Request.Resources)
        {
            var target = resource.Target.TargetPath; var original = originals[target];
            var journal = Assert.Single(session.Resources, item => item.Address == resource.Target.Address).JournalId;
            var location = backup ? Path.Combine(Path.GetDirectoryName(target)!, ".nexus-" + journal.ToString("D"), "before") : target;
            var actual = new FileTreeScanner(SnapshotBudget.Default).Capture(location, default);
            Assert.Equal(original.Tree.Shape, actual.Shape); Assert.Equal(original.Tree.Entries, actual.Entries);
            Assert.Equal(original.Objects, PhysicalTreeIdentity.Capture(location, actual));
        }
    }
    private static void AssertSaved(ConfigurationRunFixture fixture, long revision, string expected)
    {
        var store = new ConfigurationRevisionStore(fixture.Source.Instance, fixture.Source.Database, new());
        Assert.All(fixture.Request.Resources, item => { var saved = store.Read(item.Target.Address)!; Assert.Equal(revision, saved.Revision); Assert.Equal(expected, fixture.Read(saved.Tree)); });
    }
    private static void AssertRawRetained(RuntimeInstance instance, StateDatabase database, BlobReference raw)
    {
        var blobs = new ImmutableBlobStore(instance, new());
        var plan = new CleanupPlanCodec(instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), [raw]);
        var cleanup = new BlobCleanup(instance, blobs, new(instance, new(), database, blobs));
        Assert.Equal(CleanupItemState.Referenced, cleanup.Inspect(Assert.Single(plan.Items)));
        Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(Assert.Single(plan.Items))); blobs.Verify(raw);
    }
    private static async Task<JsonElement> WaitBoundaryAsync(string report)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(45))
        {
            if (File.Exists(report))
            {
                try { using var document = JsonDocument.Parse(File.ReadAllBytes(report)); return document.RootElement.Clone(); }
                catch (Exception error) when (error is JsonException or IOException) { }
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("The real run owner did not reach its bounded recovery boundary");
    }
}
