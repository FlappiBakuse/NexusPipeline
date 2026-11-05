using NexusPipeline.Core.Operations;

namespace NexusPipeline.Core.Settings;

public sealed record HostSettingsChange(long ExpectedRevision, HostSettings Values);
public sealed record SettingsSaveResult(Guid OperationId, HostSettingsSnapshot Saved, bool IsNew)
{
    public StartupEffectStatus? StartupEffect { get; init; }
}

public interface IHostSettingsStore
{
    HostSettingsSnapshot Read();
    SettingsSaveResult Commit(OperationIdentity operation, HostSettingsChange change, DateTimeOffset acceptedAt);
}
