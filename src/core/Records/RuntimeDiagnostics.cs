using NexusPipeline.Core.Execution;

namespace NexusPipeline.Core.Records;

public sealed record RuntimeDiagnosticDetails(string FrameworkVersion, string ProcessArchitecture,
    string RecoveryState, long DefinedQueueCount, long WaitingOccurrenceCount, long InstalledExtensionCount,
    long ManagedExtensionCount, long DataExtensionCount, IReadOnlyList<DiagnosticRunEvent> SafeLogTail)
{
    public long OpenConfigurationCount { get; init; }
    public long PendingExtensionCount { get; init; }
    public long PendingUpdateCount { get; init; }
    public IReadOnlyList<DiagnosticCheck> Checks { get; init; } = [];
    public IReadOnlyList<DiagnosticExtension> Extensions { get; init; } = [];
}

public sealed record DiagnosticCheck(string Id, string Status, string Code, long? Count = null);
public sealed record DiagnosticExtension(string Id, string Version, string Kind, bool? Enabled = null,
    bool? Active = null, bool? HasFailure = null);

public sealed record DiagnosticRunEvent(DateTimeOffset EndedAtUtc, EngineOutcome Engine,
    BusinessOutcome Business, LifecycleOutcome Lifecycle, RecoveryOutcome Recovery);

public enum DiagnosticAdmissionState { Ready, Blocked, ChangedDuringRead }

public sealed record RuntimeDoctorReport(string Format, int Version, Guid ReportId, Guid InstanceId,
    DateTimeOffset CapturedAtUtc, DiagnosticAdmissionState Admission, long RecoveryRevision,
    int IsolatedResourceCount, int HeldResourceCount, long PendingOperationCount,
    long AttemptReceiptCount, long CompletedRunCount, RuntimeDiagnosticDetails? Details = null);

public readonly record struct RuntimeStatus(Guid InstanceId, DiagnosticAdmissionState Admission,
    long RecoveryRevision, long PendingOperationCount);

public interface IRuntimeDoctor
{
    RuntimeDoctorReport Read(CancellationToken cancellationToken = default);
    RuntimeDoctorReport ReadSupport(CancellationToken cancellationToken = default);
    RuntimeStatus ReadStatus(CancellationToken cancellationToken = default);
}
