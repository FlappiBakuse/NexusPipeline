using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Extensions;
using System.Runtime.Versioning;
using System.Text.Json;

namespace NexusPipeline.Host.Control.Configuration;

[SupportedOSPlatform("windows")]
internal sealed class ProjectUseCases(ProjectCompilationService compilation, ProjectPreviewService previews, ContractReader contracts,
    ScriptProjectService? scripts = null)
{
    internal readonly struct Command
    {
        private Command(string action, JsonElement body, bool validated) { Action = action; Body = body; IsValidated = validated; }
        internal string Action { get; }
        internal JsonElement Body { get; }
        internal bool IsValidated { get; }

        internal static Command Borrow(string action, JsonElement body) => new(action, body, false);

        internal static Command Read(ContractReader contracts, string action, JsonElement body)
        {
            contracts.RequireParsedSchema(body, "control-project-command.schema.json");
            if (body.GetProperty("action").GetString() != action) throw new ControlRequestException(400, "project_action_mismatch");
            return new(action, body, true);
        }
    }

    internal Command Validate(string action, JsonElement body) => Command.Read(contracts, action, body);

    internal async ValueTask<object> ExecuteAsync(AuthenticatedControlRequest request, Command command, CancellationToken cancellation,
        Action? requireOwner = null, string? previewSubject = null, CancellationToken ownerLifetime = default,
        ProjectBindingDraft? bindingDraft = null, BindingDrafts? drafts = null)
    {
        var action = command.Action;
        var body = command.Body;
        void Current()
        {
            request.Require(action == "select" ? ControlCapability.ConfigurationEdit : ControlCapability.ConfigurationRead);
            if (action == "result") request.Require(ControlCapability.OperationsRead);
            if (action == "result" && body.TryGetProperty("finish", out var close) && close.ValueKind == JsonValueKind.True)
                request.Require(ControlCapability.ConfigurationEdit);
            if (action == "select" && body.TryGetProperty("protectedInputs", out var secretEdits) &&
                secretEdits.ValueKind == JsonValueKind.Array && secretEdits.GetArrayLength() > 0)
                request.Require(ControlCapability.SecretWrite);
            requireOwner?.Invoke();
        }
        Current();
        // The direct HTTP route already validates the command before checking optional capabilities.
        if (!command.IsValidated) _ = Validate(action, body);
        try
        {
            var subject = previewSubject ?? request.SubjectId;
            if (action == "page") return previews.Read(subject, Id(body, "previewId"), body.GetProperty("asset").GetString()!, Current);
            if (action == "release") return new { released = previews.Release(subject, Id(body, "previewId")) };
            if (body.TryGetProperty("scriptId", out _))
                return await new ScriptProjectCommands(scripts ?? throw new IOException("project_script_service_unavailable"), previews)
                    .ExecuteAsync(request, action, body, subject, Current, cancellation, ownerLifetime);
            if (action == "context")
            {
                var context = compilation.ReadContext(Id(body, "bindingId"), Current);
                if (context.ProtectedInputs.Count > 0) request.Require(ControlCapability.SecretStatus);
                return context;
            }
            var binding = Id(body, "bindingId"); var revision = body.GetProperty("expectedBindingRevision").GetInt64();
            var extension = body.GetProperty("extensionId").GetString()!;
            if (action == "discover") return await compilation.DiscoverAsync(binding, revision, extension, body.GetProperty("selection"), Current, cancellation, bindingDraft);
            if (bindingDraft is not null && action is "select" or "result")
            {
                using var edits = ProjectProtectedEdits.Read(body, action == "select");
                var operation = Id(body, "operationId");
                var outcome = await compilation.DraftOperationAsync(drafts!, bindingDraft, operation, request.SubjectId, binding,
                    body.GetProperty("expectedSelectionRevision").GetInt64(), extension, body.GetProperty("providerId").GetString()!,
                    body.GetProperty("selection"), edits, action == "select", body.TryGetProperty("finish", out var finish) && finish.GetBoolean(),
                    Current, cancellation);
                return new { OperationId = operation, Found = outcome == "staged", Pending = outcome == "pending", Outcome = outcome,
                    Revision = outcome == "staged" ? 1 : 0, CandidateId = outcome == "staged" ? operation : (Guid?)null };
            }
            if (action == "result") return await ResultAsync(request, body, Current, cancellation);
            if (action == "preview") return await previews.PrepareAsync(compilation, subject, binding, revision, extension, Current,
                cancellation, ownerLifetime, body.TryGetProperty("selection", out var draft) ? draft : null, bindingDraft);
            if (action != "select") throw new ControlRequestException(404, "control_not_found");
            using var protectedInputs = ProjectProtectedEdits.Read(body, true);
            var result = await compilation.SaveAsync(Id(body, "operationId"), request.SubjectId, binding, revision,
                body.GetProperty("expectedSelectionRevision").GetInt64(), extension, body.GetProperty("selection"), Current, DateTimeOffset.UtcNow, cancellation,
                protectedInputs.Values, body.TryGetProperty("providerId", out var providerId) ? providerId.GetString() : null);
            return new { result.OperationId, result.Saved.Revision, result.IsNew };
        }
        catch (ConfigurationException error) { throw new ControlRequestException(409, error.Message); }
        catch (IOException error) when (error.Message.StartsWith("project_", StringComparison.Ordinal) ||
            error.Message.StartsWith("compiler_", StringComparison.Ordinal) || error.Message == "extension_adapter_runtime_unavailable")
        { throw new ControlRequestException(409, error.Message); }
    }

    private async ValueTask<object> ResultAsync(AuthenticatedControlRequest request, JsonElement body, Action current, CancellationToken cancellation)
    {
        using var edits = ProjectProtectedEdits.Read(body, false);
        request.Require(ControlCapability.OperationsRead);
        var id = Id(body, "operationId"); var binding = Id(body, "bindingId");
        var revision = body.GetProperty("expectedBindingRevision").GetInt64(); var extension = body.GetProperty("extensionId").GetString()!;
        var provider = body.TryGetProperty("providerId", out var named) ? named.GetString()! :
            compilation.ReadContext(binding, current, readSelection: false).ProviderId;
        var expected = body.GetProperty("expectedSelectionRevision").GetInt64();
        var outcome = body.TryGetProperty("finish", out var finish) && finish.GetBoolean()
            ? await compilation.FinishAsync(id, request.SubjectId, binding, revision, expected, extension, provider,
                body.GetProperty("selection"), edits.Intents, current, cancellation)
            : compilation.ReadOutcome(id, request.SubjectId, binding, revision, expected, extension, provider,
                body.GetProperty("selection"), edits.Intents, current);
        return new { OperationId = id, Found = outcome.Outcome == "committed", Pending = outcome.Outcome == "pending",
            outcome.Outcome, Revision = outcome.Saved?.Saved.Revision };
    }

    internal ScriptProjectContext ReadScriptContext(AuthenticatedControlRequest request, Guid scriptId, string requiredProvider, Action current)
    {
        request.Require(ControlCapability.ConfigurationRead); current();
        try { return (scripts ?? throw new IOException("project_script_service_unavailable")).ReadContext(scriptId, current, requiredProvider); }
        catch (IOException error) when (error.Message.StartsWith("project_", StringComparison.Ordinal))
        { throw new ControlRequestException(409, error.Message); }
    }

    private static Guid Id(JsonElement body, string name) => ControlRequestBody.Uuid(body.GetProperty(name));

    internal ProjectEditorContext ReadContext(AuthenticatedControlRequest request, Guid bindingId, string requiredProvider, Action current,
        bool readSelection = true, ProjectBindingDraft? draft = null)
    {
        request.Require(ControlCapability.ConfigurationRead); current();
        try
        {
            var result = compilation.ReadContext(bindingId, current, requiredProvider, readSelection, draft);
            if (result.ProtectedInputs.Count > 0) request.Require(ControlCapability.SecretStatus);
            return result;
        }
        catch (IOException error) when (error.Message.StartsWith("project_", StringComparison.Ordinal))
        { throw new ControlRequestException(409, error.Message); }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ScriptProjectCommands(ScriptProjectService scripts, ProjectPreviewService previews)
{
    internal async ValueTask<object> ExecuteAsync(AuthenticatedControlRequest request, string action, JsonElement body,
        string subject, Action current, CancellationToken cancellation, CancellationToken ownerLifetime)
    {
        var script = ControlRequestBody.Uuid(body.GetProperty("scriptId"));
        if (action == "context") return scripts.ReadContext(script, current);
        var revision = body.GetProperty("expectedScriptRevision").GetInt64();
        var extension = body.GetProperty("extensionId").GetString()!;
        if (action == "discover") return await scripts.DiscoverAsync(script, revision, extension, body.GetProperty("selection"), current, cancellation);
        if (action == "preview")
        {
            var selection = body.TryGetProperty("selection", out var draft) ? draft :
                scripts.ReadContext(script, current).Selection ?? throw new IOException("project_selection_required");
            return await previews.PrepareScriptAsync(scripts, subject, script, revision, extension, selection, current, cancellation, ownerLifetime);
        }
        var operation = ControlRequestBody.Uuid(body.GetProperty("operationId"));
        var expected = body.GetProperty("expectedSelectionRevision").GetInt64();
        var provider = body.TryGetProperty("providerId", out var named) ? named.GetString()! : scripts.ReadContext(script, current).ProviderId;
        if (action == "result")
        {
            var result = scripts.ReadAccepted(operation, request.SubjectId, script, revision, expected, extension, provider, body.GetProperty("selection"), current);
            return new { OperationId = operation, Found = result is not null, Pending = result is null,
                Outcome = result is null ? "pending" : "committed", Revision = result?.Revision };
        }
        if (action != "select") throw new ControlRequestException(404, "control_not_found");
        var saved = await scripts.SaveAsync(operation, request.SubjectId, script, revision, expected, extension,
            body.GetProperty("selection"), current, cancellation, provider);
        return new { saved.OperationId, saved.Revision, saved.IsNew };
    }
}
