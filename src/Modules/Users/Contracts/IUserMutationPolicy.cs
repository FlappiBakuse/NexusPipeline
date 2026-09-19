using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users;

namespace NexusPipeline.Modules.Users.Contracts;

internal sealed record UserMutationBlock(string Code, string Message);

/// <summary>Scheduler, execution-lease and plugin availability policy owned by Host adapters.</summary>
internal interface IUserMutationPolicy
{
    UserMutationBlock? CheckUser(NexusUser user);

    UserMutationBlock? CheckBinding(string userId, string scriptId);

    UserMutationBlock? CheckScript(string scriptId);

    string? CheckScriptPluginAvailability(ScriptInstance script);
}
