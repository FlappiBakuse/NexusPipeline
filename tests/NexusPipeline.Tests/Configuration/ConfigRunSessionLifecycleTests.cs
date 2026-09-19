using Xunit;
using NexusPipeline.Modules.Configuration.Exchange;
namespace NexusPipeline.Tests.Configuration;


public sealed class ConfigRunSessionLifecycleTests
{

    [Fact]
    public void ConfigRunSession_ProvidesExplicitLifecycleBoundary()
    {
        var session = new ConfigRunSession("script", userKey: null, configPath: "", hasJudgeScript: false);

        Assert.False(session.IsPrepared);
        Assert.True(session.Prepare(out string? error));
        Assert.Null(error);
        Assert.False(session.IsPrepared);
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
}
