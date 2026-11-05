using System.Collections.Immutable;

namespace NexusPipeline.Core.Execution;

public enum TargetMemberRole { Unknown, NecessaryWriter, Game, Launcher }
public enum TargetMemberState { Unknown, Living, Stopped }
public enum TargetRetentionAuthority { None, FrozenHostGame, VerifiedExtensionLauncher }
public enum TargetInventoryState { Unknown, Complete }
public enum ExecutionTargetBoundary { Unproven, AwaitNecessaryStop, FullStopped, NecessaryStoppedWithRetained }

public sealed record ExecutionTargetMember(string IdentityKey, TargetMemberRole Role,
    TargetMemberState State, TargetRetentionAuthority RetentionAuthority);
public sealed record TargetBoundaryAssessment(ExecutionTargetBoundary Boundary,
    ImmutableArray<ExecutionTargetMember> Members);

public static class TargetBoundaryPolicy
{
    public static TargetBoundaryAssessment Assess(TargetInventoryState inventory, int accountingTotal,
        IReadOnlyList<ExecutionTargetMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var frozen = members.ToImmutableArray();
        if (!Enum.IsDefined(inventory) || accountingTotal is < 0 or > 4096 || frozen.Length > 4096 ||
            frozen.Any(member => member is null || !Key(member.IdentityKey) || !Enum.IsDefined(member.Role) ||
                !Enum.IsDefined(member.State) || !Enum.IsDefined(member.RetentionAuthority)) ||
            frozen.Select(member => member.IdentityKey).Distinct(StringComparer.Ordinal).Count() != frozen.Length)
            throw new ArgumentException("execution_target_boundary_invalid");
        var result = ExecutionTargetBoundary.Unproven;
        if (inventory != TargetInventoryState.Complete || accountingTotal != frozen.Length ||
            frozen.Any(member => member.State == TargetMemberState.Unknown))
            return new(result, frozen);
        if (frozen.Any(member => member.Role is TargetMemberRole.NecessaryWriter or TargetMemberRole.Unknown &&
            member.State == TargetMemberState.Living))
            return new(ExecutionTargetBoundary.AwaitNecessaryStop, frozen);
        if (frozen.All(member => member.State == TargetMemberState.Stopped))
            return new(ExecutionTargetBoundary.FullStopped, frozen);
        var retained = frozen.Where(member => member.State == TargetMemberState.Living);
        if (retained.All(member => member.Role == TargetMemberRole.Game &&
                member.RetentionAuthority == TargetRetentionAuthority.FrozenHostGame ||
            member.Role == TargetMemberRole.Launcher &&
                member.RetentionAuthority == TargetRetentionAuthority.VerifiedExtensionLauncher))
            result = ExecutionTargetBoundary.NecessaryStoppedWithRetained;
        return new(result, frozen);
    }

    private static bool Key(string? value) => value is { Length: >= 1 and <= 160 } && !value.Any(char.IsControl);
}
