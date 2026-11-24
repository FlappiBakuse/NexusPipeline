using NexusPipeline.Core.Control;
using NexusPipeline.Core.Execution;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Execution;
using System.Runtime.Versioning;
using System.Text.Json;

namespace NexusPipeline.Host.Control.Execution;

internal static class ExecutionControlCommand
{
    internal static ExecutionSelectionRequest Selection(JsonElement root)
    {
        ControlRequestBody.RequireMembers(root, "format", "version", "target", "expectedCatalogRevision", "expectedSettingsRevision");
        if (root.GetProperty("format").GetString() != "nexus.control.execution-selection" ||
            root.GetProperty("version").GetInt32() != 1) throw Invalid();
        var target = root.GetProperty("target");
        if (target.ValueKind != JsonValueKind.Object || !target.TryGetProperty("kind", out var kind) ||
            kind.ValueKind != JsonValueKind.String) throw Invalid();
        NexusPipeline.Core.Execution.ExecutionTarget selected = kind.GetString() switch
        {
            "binding" => Binding(target),
            "projectInitialization" => new ProjectInitializationTarget(Binding(target).BindingId),
            "script" => Script(target),
            "scriptUser" => ScriptUser(target),
            "queue" => Queue(target),
            _ => throw Invalid()
        };
        if (!root.GetProperty("expectedCatalogRevision").TryGetInt64(out var catalog) || catalog < 0 ||
            !root.GetProperty("expectedSettingsRevision").TryGetInt64(out var settings) || settings < 0) throw Invalid();
        return new(selected, catalog, settings);
    }
    private static BindingTarget Binding(JsonElement target)
    { ControlRequestBody.RequireMembers(target, "kind", "bindingId"); return new(ControlRequestBody.Uuid(target.GetProperty("bindingId"))); }
    private static ScriptTarget Script(JsonElement target)
    { ControlRequestBody.RequireMembers(target, "kind", "scriptId"); return new(ControlRequestBody.Uuid(target.GetProperty("scriptId"))); }
    private static ScriptUserTarget ScriptUser(JsonElement target)
    {
        ControlRequestBody.RequireMembers(target, "kind", "scriptId", "userId");
        return new(ControlRequestBody.Uuid(target.GetProperty("scriptId")), ControlRequestBody.Uuid(target.GetProperty("userId")));
    }
    private static QueueTarget Queue(JsonElement target)
    { ControlRequestBody.RequireMembers(target, "kind", "queueId"); return new(ControlRequestBody.Uuid(target.GetProperty("queueId"))); }
    private static ControlRequestException Invalid() => new(400, "execution_request_invalid");
}

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
            canPrepareInitialConfiguration = explanation.Items.Any(item => item.Source.Script.Kind == NexusPipeline.Core.Automation.AutomationKind.Generic &&
                item.Disposition == ExecutionExplanationDisposition.Blocked && item.ReasonCode == "configuration_required"),
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
