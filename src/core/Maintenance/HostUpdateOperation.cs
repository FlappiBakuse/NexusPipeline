namespace NexusPipeline.Core.Maintenance;

public enum HostUpdateTiming { Now, NextStartup, WhenIdle }
public enum UpdateWindowMode { Desktop, Browser, Headless }
public sealed record UpdateWindowIntent(UpdateWindowMode Mode, bool Visible, string Route);
public enum HostUpdateState { Pending, Starting, Completed, Cancelled, Quarantined }
public enum HostUpdateOutcome { Updated, OriginalRestored, NotExecuted }
public sealed record HostUpdateRequest(Guid OperationId, string Subject, UpdateFeedIdentity Feed, string HostVersion,
    HostUpdateTiming Timing, UpdateWindowIntent Window);
public sealed record HostUpdateOperation(Guid OperationId, string HostVersion, HostUpdateTiming Timing, HostUpdateState State,
    Guid? JournalId, HostUpdateOutcome? Outcome = null);
