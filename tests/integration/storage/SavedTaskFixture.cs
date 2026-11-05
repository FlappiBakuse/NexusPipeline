using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Notifications;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class SavedTaskFixture : IAsyncDisposable
{
    internal ExecutionAdmissionFixture Base { get; } = new();
    internal NotificationCatalogService Catalog { get; }
    internal Guid TaskId { get; } = Guid.NewGuid();
    internal AcceptedNotificationQueue Queue { get; }
    private readonly ResourceOperationLease _queueRoot;
    internal NotificationTaskInput Input { get; private set; } = null!;
    internal NotificationSender Sender { get; }
    internal TaskTestService Tests { get; }

    internal SavedTaskFixture()
    {
        var source = Base.Source;
        Catalog = new(source.Instance, source.Database, source.Lease, Base.Resources, Base.Index, new());
        Tests = new(source.Instance, source.Database, new(), source.Lease);
        _queueRoot = Base.Resources.TryAcquire(Guid.NewGuid(),
            [new("notifications:queue", ResourceClaimMode.Mutation)]).Lease ?? throw new IOException("fixture_queue_blocked");
        Queue = Base.Resources.RegisterNotificationQueue(_queueRoot);
        _queueRoot.Dispose();
        Sender = new(source.Instance, source.Database, new(), source.Lease,
            new(source.Instance, source.Database, source.Lease, Base.Resources, Base.Index, new()));
    }

    internal async Task Save(Uri sink, bool enabled = true)
    {
        Input = new("saved only", "", NotificationTaskKind.Webhook, enabled, NotificationChannelConfiguration.Default with {
            WebhookKind = NotificationWebhookKind.Generic, WebhookTemplate = "{\"text\":{text}}" });
        using var url = new SecretValue(sink.AbsoluteUri);
        _ = await Catalog.SaveAsync(Guid.NewGuid(), "local:saved-test", 0, TaskId, 0, Input,
            [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url)]);
    }

    internal TaskTestAcceptance Enqueue(Guid operation, string subject = "local:saved-test")
    {
        using var root = Base.Resources.TryAcquire(operation, TaskTestService.AdmissionClaims).Lease;
        Assert.NotNull(root);
        return Tests.EnqueueOwned(root, Base.Resources, Queue, operation, subject, 1, TaskId, 1);
    }

    internal async Task<NotificationDeliveryState> Send(TaskTestAcceptance saved)
    {
        var snapshot = new NotificationDeliveryCodec(Base.Source.Instance, new()).Read(saved.Delivery);
        using var root = Base.Resources.TryContinueQueue(Queue, snapshot.Document.OperationId).Lease;
        Assert.NotNull(root);
        return await Sender.SendOwnedAsync(root, Base.Resources, saved.Delivery);
    }

    public async ValueTask DisposeAsync()
    {
        await Sender.DisposeAsync(); Queue.Dispose(); Base.Dispose();
    }
}
