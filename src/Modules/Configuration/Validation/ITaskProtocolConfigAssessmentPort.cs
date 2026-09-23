using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Users.Contracts;

namespace NexusPipeline.Modules.Configuration.Validation;

/// <summary>Host-composition port for the execution-owned read-only task assessment.</summary>
internal interface ITaskProtocolConfigAssessmentPort
{
    Task<TaskPlan?> RunAsync(
        ResolvedScriptSpec spec,
        ResolvedScriptUser user,
        string trigger,
        CancellationToken token = default);

    IReadOnlyList<ConfigValidationDiagnostic> ToDiagnostics(TaskPlan plan, ResolvedScriptUser user);

    ConfigValidationDiagnostic Error(ResolvedScriptSpec spec, ResolvedScriptUser user, string message);
}
