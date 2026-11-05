namespace NexusPipeline.Core.Records;

public enum DiagnosticAdmissionState { Ready, Blocked, ChangedDuringRead }

public sealed record RuntimeDoctorReport(string Format, int Version, Guid ReportId, Guid InstanceId,
    DateTimeOffset CapturedAtUtc, DiagnosticAdmissionState Admission, long RecoveryRevision,
    int IsolatedResourceCount, int HeldResourceCount, long PendingOperationCount,
    long AttemptReceiptCount, long CompletedRunCount, RuntimeDiagnosticDetails? Details = null);

public interface IRuntimeDoctor
{
    RuntimeDoctorReport Read(CancellationToken cancellationToken = default);
    RuntimeDoctorReport ReadSupport(CancellationToken cancellationToken = default);
}
