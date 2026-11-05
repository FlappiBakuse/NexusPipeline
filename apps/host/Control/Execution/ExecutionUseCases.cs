using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Execution;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Host.Control.Execution;

[SupportedOSPlatform("windows")]
internal sealed class ExecutionUseCases(ExecutionAdmissionService admission, ExecutionControlService control,
    LocalCompletionAuthorization localAuthorization)
{
    internal object Preview(AuthenticatedControlRequest request, ExecutionSelectionRequest selection)
    {
        request.Require(ControlCapability.ExecutionPreview);
        using var draft = admission.DryRun(selection, DateTimeOffset.UtcNow);
        var explanation = draft.Explanation;
        var plan = draft.ReadPlan()?.ReadWire();
        request.Require(ControlCapability.ExecutionPreview);
        return new
        {
            draft.PlanDigest, explanation.HasRunnable, explanation.HasBlockedItems, explanation.ReasonCode,
            explanation.Selection.CatalogRevision, explanation.Selection.SettingsRevision,
            completionAction = explanation.Selection.CompletionAction,
            groups = explanation.Groups,
            items = explanation.Items.Select(item => new
            {
                item.Source.Ordinal, bindingId = item.Source.Binding.Id, scriptId = item.Source.Script.Id,
                userId = item.Source.User.Id, item.Disposition, item.ReasonCode,
                item.Source.SuccessfulRunsToday, item.Source.Binding.MaximumSuccessfulRunsPerDay,
                sourceRevisions = item.Source.SourceRevisions
            }).ToArray(),
            resources = plan?.PhysicalClaims.Select(item => new { item.ResourceKey, item.Mode }).ToArray()
        };
    }

    internal ExecutionControlAcceptance Start(AuthenticatedControlRequest request, Guid operationId,
        ExecutionSelectionRequest selection)
    {
        request.Require(ControlCapability.ExecutionStart);
        if (request.Origin != ControlOrigin.Local) return control.Start(operationId, request.SubjectId, selection, DateTimeOffset.UtcNow);
        var accepted = localAuthorization.StartLocal(admission, operationId, request.SubjectId, selection, DateTimeOffset.UtcNow);
        return control.DescribeAccepted(accepted, request.SubjectId);
    }

    internal ExecutionControlAcceptance Cancel(AuthenticatedControlRequest request, Guid operationId, Guid startOperationId,
        Guid runId, Guid attemptId)
    {
        request.Require(ControlCapability.ExecutionCancel);
        return control.Cancel(operationId, request.SubjectId, startOperationId, runId, attemptId, DateTimeOffset.UtcNow);
    }
}
