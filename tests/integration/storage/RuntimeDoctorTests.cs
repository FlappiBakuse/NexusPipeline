using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class RuntimeDoctorTests
{
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
        Assert.Same(original, recovery.Current); Assert.Equal(before, records.ReadPage(new()).Items);
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
