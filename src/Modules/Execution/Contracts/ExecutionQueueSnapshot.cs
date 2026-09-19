using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users;
namespace NexusPipeline.Modules.Execution.Contracts;


internal sealed record ExecutionQueueSnapshot(
    DispatchQueue Queue,
    IReadOnlyList<ScriptInstance> Scripts,
    IReadOnlyList<NexusUser>? Users = null);
