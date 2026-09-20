using NexusPipeline.Modules.Scripts;

namespace NexusPipeline.Modules.Scripts.Contracts;

/// <summary>Typed mutation boundary for script definitions.</summary>
internal interface IScriptMutationState
{
    ScriptInstance? Find(string id);

    IReadOnlyList<ScriptInstance> Snapshot();

    void Mutate(Action<IList<ScriptInstance>> mutation);

    TResult Mutate<TResult>(Func<IList<ScriptInstance>, TResult> mutation);
}
