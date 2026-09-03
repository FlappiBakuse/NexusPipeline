using NexusPipeline.App.Repositories;
using NexusPipeline.App.State;
using NexusPipeline.Models;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>v0.9.7：绑定运行天数模型语义与每日递减写入器。</summary>
public class RunDaysTests
{
    private static UserScriptBinding Binding(int runDays, bool enabled = true)
    {
        return new UserScriptBinding
        {
            ScriptInstanceId = "s-" + runDays,
            Enabled = enabled,
            RunDays = runDays,
        };
    }

    [Fact]
    public void Participates_FalseWhenRunDaysZero_RegardlessOfEnabled()
    {
        Assert.True(Binding(-1).Participates);
        Assert.True(Binding(3).Participates);
        Assert.False(Binding(0).Participates);
        Assert.False(Binding(-1, enabled: false).Participates);
        Assert.False(Binding(5, enabled: false).Participates);
    }

    [Fact]
    public void DecrementDaily_DecrementsPositiveOnly_AndSavesOnce()
    {
        var users = new List<NexusUser>
        {
            new()
            {
                Id = "u1",
                Name = "甲",
                Bindings = { Binding(3), Binding(1), Binding(0), Binding(-1) },
            },
        };
        var state = new RuntimeEntityState();
        state.Mutate(mutation => mutation.Users.AddRange(users));
        int saves = 0;
        var writer = new RuntimeUserRunDaysWriter(
            state,
            _ => saves++);

        bool changed = writer.DecrementDaily();

        Assert.True(changed);
        Assert.Equal(2, users[0].Bindings[0].RunDays);
        Assert.Equal(0, users[0].Bindings[1].RunDays);
        Assert.Equal(0, users[0].Bindings[2].RunDays);
        Assert.Equal(-1, users[0].Bindings[3].RunDays);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void DecrementDaily_NoChange_SkipsSave()
    {
        var users = new List<NexusUser>
        {
            new()
            {
                Id = "u1",
                Name = "乙",
                Bindings = { Binding(0), Binding(-1) },
            },
        };
        var state = new RuntimeEntityState();
        state.Mutate(mutation => mutation.Users.AddRange(users));
        int saves = 0;
        var writer = new RuntimeUserRunDaysWriter(
            state,
            _ => saves++);

        bool changed = writer.DecrementDaily();

        Assert.False(changed);
        Assert.Equal(0, saves);
    }

    [Fact]
    public void DecrementDaily_ReachesZero_StopsParticipating()
    {
        var users = new List<NexusUser>
        {
            new()
            {
                Id = "u1",
                Name = "丙",
                Bindings = { Binding(2) },
            },
        };
        var state = new RuntimeEntityState();
        state.Mutate(mutation => mutation.Users.AddRange(users));
        var writer = new RuntimeUserRunDaysWriter(
            state,
            _ => { });

        writer.DecrementDaily();
        Assert.True(users[0].Bindings[0].Participates);

        writer.DecrementDaily();
        Assert.False(users[0].Bindings[0].Participates);
    }
}
