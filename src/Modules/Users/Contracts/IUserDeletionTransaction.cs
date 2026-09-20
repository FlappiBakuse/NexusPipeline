using NexusPipeline.Modules.Users;

namespace NexusPipeline.Modules.Users.Contracts;

/// <summary>Host-owned transaction used when deleting users with related data.</summary>
internal interface IUserDeletionTransaction
{
    UserDeletionResult Execute(string userId);
}

internal sealed record UserDeletionResult(
    bool Allowed,
    NexusUser? Removed,
    IReadOnlyList<string> RunIds,
    string? FailureCode,
    string? FailureMessage);
