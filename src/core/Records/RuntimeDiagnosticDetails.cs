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
