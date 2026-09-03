using NexusPipeline.App.Abstractions;
using NexusPipeline.App.State;
using NexusPipeline.Models;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>运行时实体状态的核心所有权、快照隔离和执行输入一致性。</summary>
public sealed class RuntimeEntityStateTests
{
    [Fact]
    public void Snapshots_AreDeepCopiesAndCannotMutateOwnedState()
    {
        var state = new RuntimeEntityState();
        var script = new ScriptInstance { Id = "s1", Name = "script" };
        var user = new NexusUser
        {
            Id = "u1",
            Name = "user",
            Bindings = { new UserScriptBinding { ScriptInstanceId = "s1" } },
        };
        state.ReplaceLoadedState(new[] { script }, Array.Empty<DispatchQueue>(), new[] { user }, scriptsAuthoritative: true);

        ScriptInstance snapshotScript = Assert.Single(state.SnapshotScripts());
        NexusUser snapshotUser = Assert.Single(state.SnapshotUsers());
        snapshotScript.Name = "changed";
        snapshotUser.Bindings[0].RunDays = 0;

        Assert.Equal("script", state.FindScript("s1")!.Name);
        Assert.Equal(-1, state.FindUser("u1")!.Bindings[0].RunDays);
    }

    [Fact]
    public void FindUser_IsCaseInsensitiveButReturnsClone()
    {
        var state = new RuntimeEntityState();
        state.Mutate(mutation => mutation.Users.Add(new NexusUser { Id = "User-1", Name = "user" }));

        NexusUser? found = state.FindUser("USER-1");

        Assert.NotNull(found);
        Assert.Equal("User-1", found!.Id);
        found.Name = "changed";
        Assert.Equal("user", state.FindUser("user-1")!.Name);
    }

    [Fact]
    public void ExecutionSnapshots_ContainOneConsistentEntityMoment()
    {
        var state = new RuntimeEntityState();
        var script = new ScriptInstance { Id = "s1", Name = "script" };
        var queue = new DispatchQueue
        {
            Id = "q1",
            Name = "queue",
            Tasks = { new QueueTask { ScriptInstanceId = "s1" } },
        };
        var user = new NexusUser
        {
            Id = "u1",
            Name = "user",
            Bindings = { new UserScriptBinding { ScriptInstanceId = "s1" } },
        };
        state.ReplaceLoadedState(new[] { script }, new[] { queue }, new[] { user }, scriptsAuthoritative: true);

        ExecutionQueueSnapshot? snapshot = state.SnapshotQueueForExecution("q1");
        Assert.NotNull(snapshot);

        Assert.Equal("queue", snapshot!.Queue.Name);
        Assert.Equal("script", Assert.Single(snapshot.Scripts).Name);
        Assert.Equal("user", Assert.Single(snapshot.Users!).Name);
    }

    [Fact]
    public void Mutate_UpdatesOwnedStateAtomicallyForConcurrentCallers()
    {
        var state = new RuntimeEntityState();
        state.Mutate(mutation => mutation.Users.Add(new NexusUser { Id = "u1", Name = "user" }));

        Parallel.For(0, 64, _ => state.Mutate(mutation => mutation.Users[0].Index++));

        Assert.Equal(64, state.FindUser("u1")!.Index);
    }
}
