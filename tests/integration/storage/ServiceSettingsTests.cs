using System.Runtime.Versioning;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage.Settings;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ServiceSettingsTests
{
    [Fact(DisplayName = "A Service Settings Replay Uses Its Original Stored Revision Without Rewinding Later Preferences")]
    public void ReplaysStoredRevision()
    {
        using var fixture = new ExecutionAdmissionFixture();
        var source = fixture.Source;
        var original = HostSettings.Default;
        var owner = new HostSettingsService(source.Instance, source.Database, source.Lease, fixture.Resources,
            new(original.Desktop.Mode, original.Service, original.Updates.PluginSource));
        var operation = Guid.NewGuid();
        var service = original.Service with { McpEnabled = true };
        var first = owner.SaveService(operation, "local:service-test", 0, service, DateTimeOffset.UtcNow);
        Assert.True(first.IsNew); Assert.Equal(1, first.Saved.Revision);
        var next = new HostSettingsChange(1, first.Saved.Values with { HistoryRetentionDays = 42 });
        owner.Save(HostSettingsStore.Identity(Guid.NewGuid(), "local:service-test", next), next, DateTimeOffset.UtcNow);
        var replay = owner.SaveService(operation, "local:service-test", 0, service, DateTimeOffset.UtcNow);
        Assert.False(replay.IsNew); Assert.Equal(first.Saved.Revision, replay.Saved.Revision);
        Assert.Equal(2, owner.Read().Saved.Revision);
        Assert.Equal(42, owner.Read().Saved.Values.HistoryRetentionDays);
        Assert.Throws<OperationConflictException>(() =>
            owner.SaveService(operation, "local:other", 0, service, DateTimeOffset.UtcNow));
        Assert.Throws<OperationConflictException>(() =>
            owner.SaveService(operation, "local:service-test", 0, service with { McpWriteEnabled = true }, DateTimeOffset.UtcNow));
        Assert.Equal(2, new HostSettingsStore(source.Database).Read().Revision);
    }
}
