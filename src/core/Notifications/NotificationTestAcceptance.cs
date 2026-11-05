namespace NexusPipeline.Core.Notifications;

public sealed record NotificationTestAcceptance(Guid OperationId, Guid TaskId, bool IsNew);
