using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class TargetBoundaryTests
{
    [Fact(DisplayName = "A Real Full Stop And A Necessary Writer Boundary Are Distinct Results")]
    public void DistinguishesStopBoundaries()
    {
        ExecutionTargetMember[] source = [Member("worker", TargetMemberRole.NecessaryWriter, TargetMemberState.Stopped),
            Member("game", TargetMemberRole.Game, TargetMemberState.Living, TargetRetentionAuthority.FrozenHostGame)];
        var result = TargetBoundaryPolicy.Assess(TargetInventoryState.Complete, 2, source);
        Assert.Equal(ExecutionTargetBoundary.NecessaryStoppedWithRetained, result.Boundary);
        source[1] = source[1] with { State = TargetMemberState.Stopped };
        Assert.Equal(TargetMemberState.Living, result.Members[1].State);
        Assert.Equal(ExecutionTargetBoundary.FullStopped,
            TargetBoundaryPolicy.Assess(TargetInventoryState.Complete, 2, source).Boundary);
    }

    [Theory(DisplayName = "Living Required Or Unclassified Members Cannot Become Retained")]
    [InlineData(TargetMemberRole.NecessaryWriter)]
    [InlineData(TargetMemberRole.Unknown)]
    public void RejectsUnclassifiedMembers(TargetMemberRole role)
    {
        var result = TargetBoundaryPolicy.Assess(TargetInventoryState.Complete, 1,
            [Member("child", role, TargetMemberState.Living)]);
        Assert.Equal(ExecutionTargetBoundary.AwaitNecessaryStop, result.Boundary);
    }

    [Theory(DisplayName = "Role Names Cannot Substitute For The Actual Role Authority")]
    [InlineData(TargetMemberRole.Game, TargetRetentionAuthority.None, ExecutionTargetBoundary.Unproven)]
    [InlineData(TargetMemberRole.Game, TargetRetentionAuthority.VerifiedExtensionLauncher, ExecutionTargetBoundary.Unproven)]
    [InlineData(TargetMemberRole.Launcher, TargetRetentionAuthority.FrozenHostGame, ExecutionTargetBoundary.Unproven)]
    [InlineData(TargetMemberRole.Launcher, TargetRetentionAuthority.VerifiedExtensionLauncher, ExecutionTargetBoundary.NecessaryStoppedWithRetained)]
    public void RequiresRoleAuthority(TargetMemberRole role,
        TargetRetentionAuthority authority, ExecutionTargetBoundary expected)
    {
        Assert.Equal(expected, TargetBoundaryPolicy.Assess(TargetInventoryState.Complete, 1,
            [Member("identity", role, TargetMemberState.Living, authority)]).Boundary);
    }

    [Theory(DisplayName = "Missing Inventory Or Unknown Stop Cannot Authorize Any Boundary")]
    [InlineData(TargetInventoryState.Unknown, 1, TargetMemberState.Stopped)]
    [InlineData(TargetInventoryState.Complete, 2, TargetMemberState.Stopped)]
    [InlineData(TargetInventoryState.Complete, 1, TargetMemberState.Unknown)]
    public void RequiresCompleteInventory(TargetInventoryState inventory,
        int accounting, TargetMemberState state)
    {
        Assert.Equal(ExecutionTargetBoundary.Unproven, TargetBoundaryPolicy.Assess(inventory, accounting,
            [Member("identity", TargetMemberRole.NecessaryWriter, state)]).Boundary);
    }

    [Fact(DisplayName = "Duplicate Identities And Unknown Enum Values Are Rejected And An Empty Complete Job Has An Honest Full Stop")]
    public void RejectsInvalidIdentities()
    {
        var member = Member("identity", TargetMemberRole.NecessaryWriter, TargetMemberState.Stopped);
        Assert.Throws<ArgumentException>(() => TargetBoundaryPolicy.Assess(TargetInventoryState.Complete, 2, [member, member]));
        Assert.Throws<ArgumentException>(() => TargetBoundaryPolicy.Assess(TargetInventoryState.Complete, 1,
            [member with { Role = (TargetMemberRole)99 }]));
        Assert.Equal(ExecutionTargetBoundary.FullStopped,
            TargetBoundaryPolicy.Assess(TargetInventoryState.Complete, 0, []).Boundary);
    }

    private static ExecutionTargetMember Member(string identity, TargetMemberRole role, TargetMemberState state,
        TargetRetentionAuthority authority = TargetRetentionAuthority.None) => new(identity, role, state, authority);
}
