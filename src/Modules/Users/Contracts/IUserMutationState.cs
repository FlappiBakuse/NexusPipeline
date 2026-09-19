using NexusPipeline.Modules.Users;

namespace NexusPipeline.Modules.Users.Contracts;

/// <summary>Typed mutation boundary for user definitions.</summary>
internal interface IUserMutationState
{
    NexusUser? Find(string id);

    IReadOnlyList<NexusUser> Snapshot();

    void Mutate(Action<UserMutationContext> mutation);

    TResult Mutate<TResult>(Func<UserMutationContext, TResult> mutation);
}

/// <summary>Only the Users collection is exposed inside one state critical section.</summary>
internal sealed class UserMutationContext
{
    internal UserMutationContext(IList<NexusUser> users)
    {
        Users = users;
    }

    internal IList<NexusUser> Users { get; }
}
