using Xunit;
using NexusPipeline.Modules.Updates;

namespace NexusPipeline.Tests.Updates;

public sealed class StartupUpdateAttemptStoreTests
{
    [Fact]
    public void AutomaticRetryCooldown_ExpiresForTheSameTargetAfterTransientFailure()
    {
        string path = Path.Combine(Path.GetTempPath(), "np-startup-update-attempt-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new StartupUpdateAttemptStore(path);
        DateTimeOffset attemptedAt = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        try
        {
            // The marker is written before download, so a network failure and a worker rollback share the same finite cooldown.
            store.Mark("0.16.6", attemptedAt);

            Assert.True(store.ShouldSuppressAutomaticTarget("0.16.6", attemptedAt + StartupUpdateAttemptStore.RetryCooldown - TimeSpan.FromTicks(1)));
            Assert.False(store.ShouldSuppressAutomaticTarget("0.16.6", attemptedAt + StartupUpdateAttemptStore.RetryCooldown));
            Assert.Null(store.ReadTargetVersion());
        }
        finally
        {
            store.Clear();
        }
    }
}
