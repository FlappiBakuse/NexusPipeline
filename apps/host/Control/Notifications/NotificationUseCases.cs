using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Host.Control.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class NotificationUseCases(Guid instanceId, NotificationCatalogService catalog,
    NotificationRuntime runtime, SecretFieldService secrets)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    internal static ControlCapability Capability(string action) => action switch
    {
        "save" or "reorder" or "limit" => ControlCapability.NotificationsWrite,
        "delete" => ControlCapability.NotificationsDelete,
        "test" => ControlCapability.NotificationsTest,
        _ => throw new ControlRequestException(400, "notification_request_invalid")
    };

    internal NotificationTaskCatalog Read(AuthenticatedControlRequest request)
    { request.Require(ControlCapability.NotificationsRead); return catalog.Read(); }

    internal async Task<object> CredentialsAsync(AuthenticatedControlRequest request, Guid taskId)
    {
        request.Require(ControlCapability.NotificationsRead);
        var saved = catalog.Read();
        if (!saved.Tasks.Any(task => task.Id == taskId)) throw new ControlRequestException(404, "notification_not_found");
        var fields = new List<object>(5);
        foreach (var field in Enum.GetValues<NotificationSecretField>())
        {
            request.Require(ControlCapability.NotificationsRead);
            var state = await secrets.GetStateAsync(SecretFieldKey.NotificationTask(instanceId, taskId, field), CancellationToken.None);
            fields.Add(new { field = NotificationSecretFields.Name(field), state.Revision, state.IsConfigured, state.IsReadable });
        }
        request.Require(ControlCapability.NotificationsRead);
        return new { taskId, catalogRevision = saved.Revision, fields };
    }

    internal async Task<object> ExecuteAsync(AuthenticatedControlRequest request, NotificationControlCommand command)
    {
        request.Require(Capability(command.Action));
        return command.Action switch
        {
            "save" => await catalog.SaveAsync(command.OperationId, request.SubjectId, command.CatalogRevision,
                command.TaskId, command.TaskRevision, command.Input!, command.Secrets, CancellationToken.None),
            "delete" => await catalog.DeleteAsync(command.OperationId, request.SubjectId, command.CatalogRevision,
                command.TaskId, command.TaskRevision, CancellationToken.None),
            "reorder" => await catalog.ReorderAsync(command.OperationId, request.SubjectId, command.CatalogRevision,
                command.Order, CancellationToken.None),
            "limit" => await catalog.SetMaximumAsync(command.OperationId, request.SubjectId, command.CatalogRevision,
                command.Maximum, CancellationToken.None),
            "test" => runtime.EnqueueTaskTest(command.OperationId, request.SubjectId, command.CatalogRevision,
                command.TaskId, command.TaskRevision),
            _ => throw new ControlRequestException(400, "notification_request_invalid")
        };
    }
}
