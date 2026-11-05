using System.Text.Json;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Extensibility.Notifications;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using WireSubmission = NexusPipeline.Extensibility.Protocol.NotificationSubmission;
using CoreSubmission = NexusPipeline.Core.Notifications.NotificationSubmission;

namespace NexusPipeline.Platform.Extensions;

public sealed class NotificationSubmissionPort : INotificationSubmissionPort
{
    private readonly ExtensionScope _scope;
    private readonly INotificationSubmissionAuthorization _authorization;
    private readonly INotificationSubmissionQueue _queue;
    private readonly ContractReader _reader;
    private readonly bool _declared;

    public NotificationSubmissionPort(ExtensionManifest manifest, ExtensionScope scope, INotificationSubmissionAuthorization authorization,
        INotificationSubmissionQueue queue, ContractReader reader)
    {
        var admitted = new ExtensionManifestAdmission(reader).ReadCurrent(JsonSerializer.SerializeToUtf8Bytes(manifest), scope.Activation.ExtensionId);
        _scope = scope; _authorization = authorization; _queue = queue; _reader = reader;
        _declared = admitted.Capabilities.Contains("notification.submit", StringComparer.Ordinal);
    }

    public async ValueTask<NotificationAcceptance> SubmitAsync(WireSubmission submission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _scope.Cancellation.ThrowIfCancellationRequested();
        if (!_scope.IsPublished) throw new UnauthorizedAccessException("extension_activation_not_published");
        if (!_declared) throw new UnauthorizedAccessException("notification_submit_capability_required");
        var validated = _reader.Read<WireSubmission>(JsonSerializer.SerializeToUtf8Bytes(submission), "notification-submission.schema.json");
        var intent = new CoreSubmission(_scope.Activation.ExtensionId, ScheduledInvocationWire.FromWire(validated.Invocation),
            Guid.Parse(validated.OperationId), validated.NotificationTaskIds.Select(Guid.Parse).ToArray(), validated.Title, validated.Body);
        _authorization.AssertAllowed(intent);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _scope.Cancellation);
        var acceptance = await _queue.AcceptAsync(intent, linked.Token).ConfigureAwait(false);
        if (acceptance.OperationId != intent.OperationId || acceptance.AcceptanceId == Guid.Empty)
            throw new IOException("notification_queue_acceptance_unbound");
        return new(acceptance.OperationId, acceptance.AcceptanceId, acceptance.IsNew);
    }
}
