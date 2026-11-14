using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Records;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Host.Control.Http;

internal static class ControlExceptionBoundary
{
    internal static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception error) when (!context.Response.HasStarted)
        {
            if (error is IOException && context.Request.Path != "/control/v1/extensions/reconcile")
            {
                var method = new System.Diagnostics.StackTrace(error, false).GetFrame(0)?.GetMethod();
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("NexusPipeline.Host")
                    .LogWarning("Control IO request rejected: {FailureType}; {Owner}; {Method}", error.GetType().Name,
                        method?.DeclaringType?.FullName, method?.Name);
            }
            if (error is IOException && context.Request.Path == "/control/v1/extensions/reconcile")
            {
                var reason = error.Message is "extension_preference_resources_unavailable" or "extension_runtime_revision_conflict" or
                    "extension_installation_consumption_unavailable" or "extension_runtime_stopping"
                    ? error.Message : "extension_reconciliation_unavailable";
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("NexusPipeline.Host")
                    .LogWarning("Extension reconciliation rejected: {Reason}; {FailureSite}", reason,
                        new System.Diagnostics.StackTrace(error, false).ToString());
            }
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
                ConfigurationException => (400, "configuration_invalid"),
                IOException => (409, "resource_unavailable"),
                _ => (500, "control_failed")
            };
            await ControlErrors.Result(status, code).ExecuteAsync(context);
        }
    }
}
