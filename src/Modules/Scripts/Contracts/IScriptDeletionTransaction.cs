namespace NexusPipeline.Modules.Scripts.Contracts;

internal sealed record ScriptDeletionResult(
    bool Allowed,
    ScriptInstance? Removed,
    IReadOnlyList<string> RunIds,
    string? FailureCode);

/// <summary>Host-owned cross-entity deletion transaction for scripts and bindings.</summary>
internal interface IScriptDeletionTransaction
{
    ScriptDeletionResult Execute(string scriptId, string source, out string? failureCode);
}
