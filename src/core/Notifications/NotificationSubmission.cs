using NexusPipeline.Core.Scheduling;

namespace NexusPipeline.Core.Notifications;

public sealed class NotificationSubmission
{
    public string ExtensionId { get; }
    public FrozenScheduledInvocation Invocation { get; }
    public Guid OperationId { get; }
    public IReadOnlyList<Guid> NotificationTaskIds { get; }
    public string Title { get; }
    public string Body { get; }

    public NotificationSubmission(string extensionId, FrozenScheduledInvocation invocation, Guid operationId, IReadOnlyList<Guid> targets, string title, string body)
    {
        Extensions.ExtensionIdentifiers.Require(extensionId);
        var ids = Array.AsReadOnly(targets.ToArray());
        if (operationId == Guid.Empty || operationId != invocation.NotificationOperationId || ids.Count is < 1 or > 99 ||
            ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Count || ids.Any(id => !invocation.NotificationTaskIds.Contains(id)) ||
            string.IsNullOrWhiteSpace(title) || title.Length > 200 || string.IsNullOrWhiteSpace(body) || body.Length > 4000)
            throw new ArgumentException("notification_submission_invalid");
        ExtensionId = extensionId; Invocation = invocation; OperationId = operationId; NotificationTaskIds = ids; Title = title; Body = body;
    }
}

public sealed record NotificationQueueAcceptance(Guid OperationId, Guid AcceptanceId, bool IsNew);

public interface INotificationSubmissionAuthorization
{
    void AssertAllowed(NotificationSubmission submission);
}

public interface INotificationSubmissionQueue
{
    ValueTask<NotificationQueueAcceptance> AcceptAsync(NotificationSubmission submission, CancellationToken cancellationToken);
}
