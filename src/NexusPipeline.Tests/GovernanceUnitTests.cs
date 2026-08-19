using NexusPipeline.Extensibility;
using NexusPipeline;
using NexusPipeline.Models;
using NexusPipeline.Plugins;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

public class GovernanceUnitTests
{
    [Fact]
    public void RunBudget_CentralizesElapsedRemainingAndCommandCap()
    {
        DateTime now = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Local);
        var budget = new RunBudget(1, now, () => now);

        Assert.Equal(60, budget.RemainingSeconds);
        Assert.False(budget.IsExpired);
        now = now.AddSeconds(12.25);
        Assert.Equal(12.25, budget.ElapsedSeconds, precision: 2);
        Assert.Equal(47.75, budget.RemainingSeconds, precision: 2);
        Assert.Equal(30, budget.RemainingCommandSeconds(30));
        Assert.Equal(48, budget.RemainingCommandSeconds(60));
        now = now.AddSeconds(48);
        Assert.True(budget.IsExpired);
        Assert.Equal(1, budget.RemainingCommandSeconds(30));
    }

    [Fact]
    public void ConfigRunSession_FinalizationOrder_IsSingleAndStable()
    {
        IReadOnlyList<ConfigRunSession.FinalizationStep> order = ConfigRunSession.BuildFinalizationOrder(
            canSync: true,
            hasJudgeScript: true,
            prepared: true);

        Assert.Equal(
            new[]
            {
                ConfigRunSession.FinalizationStep.Sync,
                ConfigRunSession.FinalizationStep.RestoreReplacements,
                ConfigRunSession.FinalizationStep.CleanupScriptArea,
                ConfigRunSession.FinalizationStep.RestoreConfig,
            },
            order);
    }

    [Fact]
    public void ConfigRunSession_FinalizationOrder_WithoutPreparedConfigSkipsSyncAndRestore()
    {
        IReadOnlyList<ConfigRunSession.FinalizationStep> order = ConfigRunSession.BuildFinalizationOrder(
            canSync: false,
            hasJudgeScript: true,
            prepared: false);

        Assert.Equal(
            new[]
            {
                ConfigRunSession.FinalizationStep.RestoreReplacements,
                ConfigRunSession.FinalizationStep.CleanupScriptArea,
            },
            order);
    }

    [Fact]
    public void RunAttemptFinalizer_GameCleanupPolicy_PreservesFailureAndForceCloseSemantics()
    {
        Assert.True(RunAttemptFinalizer.ShouldCloseGame(RunAttemptResult.Failed("failed"), forceCloseGame: false));
        Assert.True(RunAttemptFinalizer.ShouldCloseGame(RunAttemptResult.Cancelled("cancelled"), forceCloseGame: true));
        Assert.False(RunAttemptFinalizer.ShouldCloseGame(RunAttemptResult.Cancelled("cancelled"), forceCloseGame: false));
        Assert.False(RunAttemptFinalizer.ShouldCloseGame(RunAttemptResult.Success("success"), forceCloseGame: true));
    }

    [Fact]
    public void PluginCapabilityRegistry_UsesGenericCapabilityLookupAndReloadIsIdempotent()
    {
        var registry = new PluginCapabilityRegistry();
        var capability = new TestCapability();
        registry.Register("demo", capability);
        registry.RegisterKeys("demo", new[] { "probe", "emulator" });

        Assert.Single(registry.GetAll<TestCapability>(_ => true));
        Assert.True(registry.HasKey("demo", "probe", _ => true));
        Assert.Empty(registry.GetAll<TestCapability>(_ => false));
        Assert.False(registry.HasKey("demo", "probe", _ => false));

        registry.Clear();
        registry.Register("demo", capability);
        Assert.Single(registry.GetAll<TestCapability>(_ => true));
    }

    [Fact]
    public void PluginManager_LoadAll_DoesNotDuplicateBuiltInCapabilities()
    {
        PluginManager manager = RuntimeContext.Instance.Plugins;
        manager.LoadAll();
        int firstCount = manager.GetCapabilities<IEmulatorCapability>().Count;

        manager.LoadAll();
        int secondCount = manager.GetCapabilities<IEmulatorCapability>().Count;

        Assert.Equal(firstCount, secondCount);
        Assert.True(manager.HasCapability(AppSettings.EmulatorAdapterPlugin, PluginCapabilityKeys.Emulator));
    }

    private sealed class TestCapability : IPluginCapability
    {
    }
}
