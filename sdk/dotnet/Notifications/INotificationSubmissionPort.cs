using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Extensibility.Notifications;

/// <summary>Nonsecret queue acceptance metadata; it does not claim delivery to a recipient.</summary>
public sealed record NotificationAcceptance(Guid OperationId, Guid AcceptanceId, bool IsNew);

/// <summary>Submits only to configured targets selected in the frozen Host invocation.</summary>
public interface INotificationSubmissionPort
{
    /// <summary>The Host owns durable idempotency and delivery; plugins never provide arbitrary recipient addresses.</summary>
    ValueTask<NotificationAcceptance> SubmitAsync(NotificationSubmission submission, CancellationToken cancellationToken);
}
