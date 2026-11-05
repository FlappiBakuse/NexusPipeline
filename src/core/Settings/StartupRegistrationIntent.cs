namespace NexusPipeline.Core.Settings;

public enum StartupEffectState { None, Prepared, Entered, Completed, Quarantined }
public sealed record StartupEffectStatus(Guid OperationId, long SettingsRevision, bool Enable, StartupEffectState State);
