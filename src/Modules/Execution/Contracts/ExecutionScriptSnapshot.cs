using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users;
namespace NexusPipeline.Modules.Execution.Contracts;


internal sealed record ExecutionScriptSnapshot(
    ScriptInstance Script,
    IReadOnlyList<NexusUser>? Users = null);
