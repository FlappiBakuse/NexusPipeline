using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Settings;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Records;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Host.Lifecycle;

namespace NexusPipeline.Host.Control.Http;

internal static class ControlErrors
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static IResult Result(int status, string code, Guid? operationId = null) =>
        Response(status, code, operationId, Guid.NewGuid());

    private static IResult Response(int status, string code, Guid? operationId, Guid correlationId) => Results.Json(new
    {
        format = "nexus.control.error", version = 1, code,
        message = "The control request could not be completed.", correlationId, operationId
    }, Options, statusCode: status);

    internal static IResult Reported(int status, string code, string stage, Exception error, Guid? operationId = null,
        Guid? instanceId = null) => new ReportedControlError(status, code, stage, error, operationId, instanceId);

    internal static void RecordLifetime(string stage, Guid ownerId, Exception error, Guid? instanceId = null, Guid? correlationId = null)
    {
        try
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { kind = "control-lifetime.failure", stage, ownerId,
                instanceId, correlationId, causes = ExecutionFailureTrace.Describe(error) }, Options));
        }
        catch (Exception loggingFailure) when (loggingFailure is IOException or ObjectDisposedException) { }
    }

    private sealed class ReportedControlError(int status, string code, string stage, Exception error, Guid? operationId,
        Guid? instanceId) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            var correlationId = Guid.NewGuid();
            var handoff = HostHandoffDiagnostic.Read(error);
            var identifier = operationId ?? handoff?.OperationId;
            if (identifier is null && Guid.TryParseExact(context.Request.RouteValues["operationId"] as string, "D", out var routeOperation) &&
                routeOperation != Guid.Empty) identifier = routeOperation;
            var level = status >= 500 ? LogLevel.Error : LogLevel.Warning;
            try
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("NexusPipeline.Host.Control");
                if (logger.IsEnabled(level))
                    logger.Log(level, "Control failure {CorrelationId} {Stage} {Code} for {Route} {InstanceId} {OperationId} {Activity}: {Causes}",
                        correlationId, handoff?.Stage ?? stage, code, (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
                        instanceId ?? handoff?.InstanceId, identifier, handoff?.Activity,
                        JsonSerializer.Serialize(ExecutionFailureTrace.Describe(error), Options));
            }
            catch (Exception loggingFailure) when (loggingFailure is IOException or ObjectDisposedException or AggregateException) { }
            return Response(status, code, identifier, correlationId).ExecuteAsync(context);
        }
    }

    public static IResult Secret(Exception error, bool reading, Guid? operationId = null, Guid? instanceId = null) => error switch
    {
        OperationConflictException => Result(409, "operation_conflict", operationId),
        System.Text.EncoderFallbackException when reading => Result(409, "secret_unavailable", operationId),
        ArgumentException => Result(400, "secret_request_invalid", operationId),
        InvalidOperationException { Message: "secret_revision_conflict" } => Result(409, "revision_conflict", operationId),
        InvalidOperationException { Message: "secret_resource_conflict" } => Result(409, "resource_conflict", operationId),
        InvalidOperationException { Message: "secret_host_unavailable" } => Result(503, "host_unavailable", operationId),
        Exception when reading && error is IOException or System.Security.Cryptography.CryptographicException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException => Result(409, "secret_unavailable", operationId),
        IOException when CleanupFailures.Read(error).Count != 0 => Reported(409, "secret_recovery_required", "secret-cleanup", error, operationId, instanceId),
        IOException => Result(409, "secret_recovery_required", operationId),
        _ => Reported(500, "control_failed", reading ? "secret-read" : "secret-write", error, operationId, instanceId)
    };
}

internal static class ControlExceptionBoundary
{
    internal static Task InvokeAsync(HttpContext context, RequestDelegate next) => InvokeAsync(context, next, null);

    internal static async Task InvokeAsync(HttpContext context, RequestDelegate next, Guid? instanceId)
    {
        try { await next(context); }
        catch (OperationCanceledException error) when (context.RequestAborted.IsCancellationRequested)
        {
            var handoff = HostHandoffDiagnostic.Read(error);
            var identifier = handoff?.OperationId ?? instanceId ?? Guid.Empty;
            if (CleanupFailures.Read(error).Count != 0)
                ControlErrors.RecordLifetime(handoff?.Stage ?? "request-cancel-cleanup", identifier, error, instanceId ?? handoff?.InstanceId);
        }
        catch (Exception error) when (!context.Response.HasStarted)
        {
            var (status, code) = error switch
            {
                NexusPipeline.Core.Media.PreviewUnavailableException preview => (409, preview.State switch
                {
                    NexusPipeline.Core.Media.PreviewAvailability.WaitingForGame => "media_waiting_for_game",
                    NexusPipeline.Core.Media.PreviewAvailability.WindowNotReady => "media_window_not_ready",
                    NexusPipeline.Core.Media.PreviewAvailability.EmulatorNotReady => "media_emulator_not_ready",
                    _ => "media_preview_unavailable"
                }),
                ControlRequestException request => (request.Status, request.Message),
                OperationConflictException => (409, "operation_conflict"),
                UnauthorizedAccessException => (403, "access_denied"),
                AutomationConflictException => (409, "revision_conflict"),
                AutomationDefinitionException { Message: "automation_resource_conflict" } => (409, "resource_conflict"),
                AutomationDefinitionException { Message: "automation_host_unavailable" } => (409, "host_unavailable"),
                InvalidOperationException { Message: "secret_revision_conflict" } => (403, "access_denied"),
                HostSettingsException { Message: "settings_revision_conflict" } => (409, "revision_conflict"),
                HostSettingsException { Message: "settings_resource_conflict" } => (409, "resource_conflict"),
                HostSettingsException { Message: "settings_host_unavailable" } => (409, "host_unavailable"),
                HostSettingsException { Message: "startup_registration_elevation_required" } => (409, "startup_elevation_required"),
                HostSettingsException => (400, "settings_invalid"),
                NotificationTaskException { Message: "notification_revision_conflict" } => (409, "revision_conflict"),
                NotificationTaskException invalid => (400, invalid.Message),
                InvalidOperationException { Message: "notification_resource_conflict" } => (409, "resource_conflict"),
                InvalidOperationException { Message: "notification_host_unavailable" } => (409, "host_unavailable"),
                InvalidOperationException { Message: "notification_revision_conflict" } => (409, "revision_conflict"),
                InvalidOperationException { Message: "media_provider_unavailable" or "media_preview_closed" or "media_capture_in_progress" } => (409, "media_unavailable"),
                JsonException or ArgumentException or InvalidDataException or AutomationDefinitionException => (400, "request_invalid"),
                RetiredContentException => (410, "record_content_retired"),
                ExecutionSelectionException { Message: "execution_catalog_revision_conflict" or "execution_settings_revision_conflict" } => (409, "revision_conflict"),
                ExecutionSelectionException { Message: "execution_resource_conflict" } => (409, "resource_conflict"),
                ExecutionSelectionException { Message: "execution_host_unavailable" } => (409, "host_unavailable"),
                ExecutionSelectionException => (400, "execution_selection_invalid"),
                ConfigurationException { Message: "configuration_revision_conflict" or "configuration_binding_revision_conflict" or
                    "configuration_patch_binding_conflict" or "configuration_source_changed" } => (409, "revision_conflict"),
                ConfigurationException { Message: "configuration_binding_missing" or "configuration_required" or "configuration_resource_missing" } => (404, "configuration_not_found"),
                ConfigurationException { Message: "configuration_resource_conflict" } => (409, "resource_conflict"),
                ConfigurationException { Message: "configuration_host_unavailable" } => (409, "host_unavailable"),
                ConfigurationException { Message: "configuration_editor_resume_unavailable" } => (409, "configuration_editor_resume_unavailable"),
                ConfigurationException { Message: "configuration_draft_missing" or "project_selection_contract_changed" } => (409, error.Message),
                ConfigurationException => (400, "configuration_invalid"),
                IOException { Message: "upstream_program_already_running" or "upstream_program_stop_unconfirmed" } => (409, error.Message),
                IOException { Message: ConfigurationRecoveryCodes.Pending or ConfigurationRecoveryCodes.Lost } => (409, error.Message),
                IOException => (409, "resource_unavailable"),
                _ => (500, "control_failed")
            };
            var report = CleanupFailures.Read(error).Count != 0 || code == "control_failed" || code == "resource_unavailable" &&
                error.Message is not ("run_log_not_active" or "run_log_subscription_limit");
            if (!report)
            {
                await ControlErrors.Result(status, code).ExecuteAsync(context);
                return;
            }
            var stage = context.Request.Path == "/control/v1/extensions/reconcile" ? error.Message switch
            {
                "extension_preference_resources_unavailable" or "extension_runtime_revision_conflict" or
                    "extension_installation_consumption_unavailable" or "extension_runtime_stopping" => error.Message,
                _ => "extension-reconciliation"
            } : "request";
            await ControlErrors.Reported(status, code, stage, error, instanceId: instanceId).ExecuteAsync(context);
        }
    }
}
