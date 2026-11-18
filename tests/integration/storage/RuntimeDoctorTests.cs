using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Records;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class RuntimeDoctorTests
{
    [Fact(DisplayName = "Control Statistics Advance For Their Own Committed Facts And Preserve Their Revision Across Unrelated Writes And Rollback")]
    public void TracksControlRevision()
    {
        var fixture = Create();
        using var owner = InstanceLease.Acquire(fixture.Instance, new());
        var database = StateDatabase.OpenCurrent(fixture.Instance, owner);
        var doctor = new RuntimeDoctor(fixture.Instance, database, new RecoveryIndex());
        long Revision() => database.ReadControlVersion()!.Value.Revision;
        var initial = doctor.ReadControlSnapshot(); var revision = Revision();
        var sequence = database.CommitSequence;
        var registry = new RecoveryJournalRegistry(database);
        _ = registry.PublishVerified(registry.ReadSnapshot().Revision);
        Assert.True(database.CommitSequence > sequence);
        Assert.Equal(revision, Revision());
        Assert.Equal(initial, doctor.ReadControlSnapshot());
        database.Write((connection, transaction) =>
        {
            HostSettingsRows.Insert(connection, transaction, new(1, HostSettings.Default));
            using var command = DatabaseConnections.Command(connection, transaction,
                "UPDATE system.settings_current SET revision=1 WHERE singleton=1; " +
                "UPDATE automation.automation_catalog_state SET revision=1 WHERE singleton=1;");
            return command.ExecuteNonQuery();
        });
        Assert.Equal(++revision, Revision());
        Assert.Equal(1, doctor.ReadControlSnapshot().SettingsRevision);
        Assert.Equal(1, doctor.ReadControlSnapshot().CatalogRevision);
        Assert.Throws<InvalidOperationException>(() => database.Write<int>((connection, transaction) =>
        {
            using var command = DatabaseConnections.Command(connection, transaction,
                "UPDATE automation.automation_catalog_state SET revision=2 WHERE singleton=1;");
            command.ExecuteNonQuery(); throw new InvalidOperationException("rollback");
        }));
        Assert.Equal(revision, Revision());
        Assert.Equal(1, doctor.ReadControlSnapshot().CatalogRevision);
        database.Write((connection, transaction) =>
        {
            OperationRows.Accept(connection, transaction,
                new OperationIdentity(Guid.NewGuid(), "control-revision", new string('a', 64), "fixture.control"), DateTimeOffset.UtcNow);
            return true;
        });
        Assert.Equal(++revision, Revision());
        Assert.Equal(1, doctor.ReadControlSnapshot().Counts.PendingOperations);
        var pending = database.ReadControlVersion()!.Value;
        new RunRecordStore(database).AppendCompleted([RecordStoreFixture.Record(1, Guid.NewGuid())]);
        Assert.Equal(++revision, Revision());
        Assert.Equal(1, doctor.ReadControlSnapshot().Counts.CompletedRuns);
        Assert.Equal(ControlStatisticsFields.Runs, database.ReadControlVersion()!.Value.ChangedSince(pending));
    }

    [Fact(DisplayName = "Control Snapshots Keep Catalog Settings And Counts Together Across Atomic Commits")]
    public async Task ReadsControlSnapshot()
    {
        var (instance, _) = Create();
        using var owner = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenCurrent(instance, owner);
        var doctor = new RuntimeDoctor(instance, database, new RecoveryIndex());
        var original = doctor.ReadControlSnapshot();
        var automation = Guid.NewGuid();
        var writer = Task.Run(() =>
        {
            for (var revision = 1; revision <= 24; revision++)
                database.Write((connection, transaction) =>
                {
                    HostSettingsRows.Insert(connection, transaction, new(revision, HostSettings.Default));
                    using var command = DatabaseConnections.Command(connection, transaction,
                        "UPDATE system.settings_current SET revision=$0 WHERE singleton=1; " +
                        "UPDATE automation.automation_catalog_state SET revision=$0 WHERE singleton=1;", revision);
                    command.ExecuteNonQuery();
                    OperationRows.Accept(connection, transaction,
                        new OperationIdentity(Guid.NewGuid(), "control-snapshot", new string('a', 64), "fixture.control"), DateTimeOffset.UtcNow);
                    RunRecordStore.PublishInside(connection, transaction, RecordStoreFixture.Record(revision, automation));
                    return true;
                });
        });
        try
        {
            do
            {
                var snapshot = doctor.ReadControlSnapshot();
                Assert.Equal(snapshot.CatalogRevision, snapshot.SettingsRevision);
                Assert.Equal(snapshot.CatalogRevision, snapshot.Counts.CompletedRuns);
                Assert.Equal(snapshot.CatalogRevision, snapshot.Counts.PendingOperations);
                Assert.Equal(0, snapshot.Counts.Attempts);
                await Task.Yield();
            } while (!writer.IsCompleted);
        }
        finally { await writer.WaitAsync(TimeSpan.FromSeconds(15)); }
        var current = doctor.ReadControlSnapshot();
        Assert.Equal(24, current.CatalogRevision);
        Assert.Equal(24, current.SettingsRevision);
        Assert.Equal(24, current.Counts.CompletedRuns);
        Assert.Equal(0, original.CatalogRevision);
        Assert.Equal(0, original.Counts.CompletedRuns);
    }

    [Fact(DisplayName = "Control Snapshots Revalidate Current Settings And Recover After Invalid Saved Values Are Corrected")]
    public void RechecksControlSettings()
    {
        var fixture = Create();
        var doctor = new RuntimeDoctor(fixture.Instance, fixture.Database, new RecoveryIndex());
        Assert.Equal(0, doctor.ReadControlSnapshot().SettingsRevision);
        Save(2);
        Assert.Equal("settings_proxy_url_required", Assert.Throws<HostSettingsException>(() => doctor.ReadControlSnapshot()).Message);
        Save(0);
        Assert.Equal(0, doctor.ReadControlSnapshot().SettingsRevision);
        void Save(int mode) => fixture.Database.Write((connection, transaction) =>
        {
            using var command = DatabaseConnections.Command(connection, transaction,
                "UPDATE system.settings_snapshots SET proxy_mode=$0,proxy_url=NULL WHERE revision=0;", mode);
            return command.ExecuteNonQuery();
        });
    }

    [Fact(DisplayName = "The Actual Read Only Diagnostic Counts Saved Facts Without Exporting Private Reasons Subjects Or Paths")]
    public void ReportsSafeCounts()
    {
        const string canary = "doctor-private-credential-canary";
        var fixture = Create();
        var records = new RunRecordStore(fixture.Database);
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        records.AppendCompleted([record]);
        var recovery = new RecoveryIndex();
        recovery.PublishVerified(0, 1, [new("private-resource-" + canary, Guid.NewGuid(), canary)]);
        recovery.BlockUnknown("https://private.invalid/" + canary);
        var original = recovery.Current;
        var before = records.ReadPage(new()).Items.ToArray();
        IRuntimeDoctor doctor = new RuntimeDoctor(fixture.Instance, fixture.Database, recovery);
        var report = doctor.ReadSupport();
        Assert.Equal(fixture.Instance.InstanceId, report.InstanceId);
        Assert.NotEqual(Guid.Empty, report.ReportId); Assert.Equal("nexus.runtime.diagnostic", report.Format); Assert.Equal(1, report.Version);
        Assert.Equal(DiagnosticAdmissionState.Blocked, report.Admission);
        Assert.Equal(original.Revision, report.RecoveryRevision); Assert.Equal(1, report.IsolatedResourceCount);
        Assert.Equal(0, report.HeldResourceCount); Assert.Equal(0, report.PendingOperationCount);
        Assert.Equal(0, report.AttemptReceiptCount); Assert.Equal(1, report.CompletedRunCount);
        var details = Assert.IsType<RuntimeDiagnosticDetails>(report.Details);
        Assert.Equal("unverified", details.RecoveryState);
        Assert.Equal(0, details.InstalledExtensionCount); Assert.Equal(0, details.WaitingOccurrenceCount);
        var log = Assert.Single(details.SafeLogTail);
        Assert.Equal(record.FinalAttempt.Engine, log.Engine); Assert.Equal(record.FinalAttempt.Business, log.Business);
        var exported = JsonSerializer.Serialize(report);
        Assert.DoesNotContain(canary, exported); Assert.DoesNotContain(fixture.Instance.RuntimeRoot, exported);
        Assert.DoesNotContain(record.AutomationId.ToString("D"), exported); Assert.InRange(exported.Length, 1, 2048);
        var current = recovery.Current;
        Assert.Equal(original.IsReady, current.IsReady); Assert.Equal(original.Revision, current.Revision);
        Assert.Equal(original.GlobalBlockReason, current.GlobalBlockReason);
        Assert.Equal(original.Isolations.ToArray(), current.Isolations.ToArray());
        Assert.Equal(original.ResourceHolds.ToArray(), current.ResourceHolds.ToArray());
        Assert.Equal(original.SnapshotCompletions.ToArray(), current.SnapshotCompletions.ToArray());
        Assert.Equal(before, records.ReadPage(new()).Items);
    }

    [Theory(DisplayName = "Unknown Or Concurrently Changed Recovery Never Produces A Ready Diagnostic")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsChangedRecovery(bool changed)
    {
        var fixture = Create();
        var recovery = new ChangingIndex(changed);
        var report = new RuntimeDoctor(fixture.Instance, fixture.Database, recovery).Read();
        Assert.Equal(changed ? DiagnosticAdmissionState.ChangedDuringRead : DiagnosticAdmissionState.Blocked, report.Admission);
        Assert.Equal(0, report.CompletedRunCount); Assert.Equal(0, report.AttemptReceiptCount);
    }

    [Fact(DisplayName = "A Foreign Database And A Cancelled Read Cannot Inspect Or Modify The Original Instance")]
    public void RejectsForeignReads()
    {
        var fixture = Create(); var other = Create(); var index = new RecoveryIndex();
        Assert.Throws<IOException>(() => new RuntimeDoctor(fixture.Instance, other.Database, index));
        var doctor = new RuntimeDoctor(fixture.Instance, fixture.Database, index);
        Assert.Throws<OperationCanceledException>(() => doctor.Read(new CancellationToken(true)));
        Assert.Equal(0, doctor.Read().CompletedRunCount);
    }

    [Theory(DisplayName = "Status Reads Current Operations With The Records Domain Unavailable")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsStatusWithoutRecords(bool changed)
    {
        var (instance, database) = Create();
        database.Accept(new(Guid.NewGuid(), "control-status", new string('a', 64), "fixture.control"), DateTimeOffset.UtcNow);
        File.Move(Path.Combine(instance.RuntimeRoot, "state", "records.db"),
            Path.Combine(instance.RuntimeRoot, "state", "records.held"));
        var status = new RuntimeDoctor(instance, database, new ChangingIndex(changed)).ReadStatus();
        Assert.Equal(instance.InstanceId, status.InstanceId);
        Assert.Equal(changed ? DiagnosticAdmissionState.ChangedDuringRead : DiagnosticAdmissionState.Blocked, status.Admission);
        Assert.Equal(3, status.RecoveryRevision);
        Assert.Equal(1, status.PendingOperationCount);
    }

    private static (RuntimeInstance Instance, StateDatabase Database) Create()
    {
        var (database, _) = RecordStoreFixture.NewStore();
        return (new InstanceRoot(new()).OpenExisting(database.RuntimeRoot), database);
    }

    private sealed class ChangingIndex(bool changed) : IRecoveryIndex
    {
        private int _reads;
        private readonly RecoveryProjection _initial = new(changed, 3, "private-unknown-reason", []);
        public RecoveryProjection Current => changed && ++_reads > 1 ? new(false, 4, "another-private-reason", []) : _initial;
        public RecoveryProjection BlockUnknown(string reasonCode) => throw new InvalidOperationException("A diagnostic must remain read-only");
    }
}
