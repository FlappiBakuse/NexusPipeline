namespace NexusPipeline.Modules.Scripts.Contracts;

internal sealed record ScriptMutationAdmissionResult(
    bool Allowed,
    IReadOnlyList<string> RunIds,
    string? FailureCode);

/// <summary>Script-facing execution lease/admission port.</summary>
internal interface IScriptMutationAdmission
{
    ScriptMutationAdmissionResult TryExecute(
        string scriptId,
        string? userName,
        Action mutation);
}
