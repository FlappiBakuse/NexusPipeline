using System.Runtime.Versioning;
using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class NotificationReceiptFixture : IDisposable
{
    private readonly ExecutionSequentialFixture _source = new(accept: false);
    internal PlanBuilderFixture Source => _source.Source;
    internal ExecutionAdmissionFixture Base => _source.Base;
    internal NotificationCatalogService Catalog { get; }
    internal NotificationDeliveryService Delivery { get; }
    internal ExecutionNotificationRegistration Registration { get; }
    internal Guid TaskId { get; } = Guid.NewGuid();
    internal ImmutableArray<Guid> TaskIds { get; private set; } = [];
    internal AttemptReceipt Receipt { get; private set; } = null!;
    internal NotificationReceiptSnapshot Snapshot { get; private set; } = null!;
    internal NotificationTaskInput Input { get; } = new("receipt task", "", NotificationTaskKind.Webhook, true,
        NotificationChannelConfiguration.Default with { WebhookKind = NotificationWebhookKind.Generic });

    internal NotificationReceiptFixture(NotificationTaskInput? input = null)
    {
        if (input is not null) Input = input;
        Catalog = new(Source.Instance, Source.Database, Source.Lease, _source.Base.Resources, _source.Base.Index, new());
        Delivery = new(Source.Instance, Source.Database, new(), Source.Lease);
        Registration = new(Source.Instance, Source.Database, new(), Source.Lease, new(Source.Instance, Source.Database, new()));
    }

    internal async Task Execute(bool emptyQueue = false, int taskCount = 1, bool queueNotifications = true,
        IReadOnlyList<NotificationSecretEdit>? edits = null, byte[]? indexedFixtureImage = null,
        IAttemptTerminalObserver? terminalObserver = null, Action? afterAccept = null, int repetitions = 1,
        bool readSnapshot = true)
    {
        if (taskCount is < 1 or > 99) throw new ArgumentOutOfRangeException(nameof(taskCount));
        TaskIds = [TaskId];
        using (var secret = new SecretValue("https://notification-fixture.invalid/source"))
            _ = await Catalog.SaveAsync(Guid.NewGuid(), "local:receipt-fixture", 0, TaskId, 0, Input,
                edits ?? [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, secret)]);
        for (var index = 1; index < taskCount; index++)
        {
            var id = Guid.NewGuid();
            using var secret = new SecretValue("https://notification-fixture.invalid/source/" + index);
            _ = await Catalog.SaveAsync(Guid.NewGuid(), "local:receipt-fixture", index, id, 0,
                Input with { Name = "receipt task " + index }, [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, secret)]);
            TaskIds = TaskIds.Add(id);
        }
        var catalog = Source.Definitions.Read();
        var changes = new AutomationChanges(catalog.Revision, [], [], [],
            catalog.Bindings.Select(item => item with { NotifyEnabled = true, NotificationTaskIds = TaskIds }).ToImmutableArray(),
            [catalog.Queues.Single() with { NotifyEnabled = queueNotifications, NotificationTaskIds = emptyQueue ? [] : TaskIds,
                ScriptIds = Enumerable.Range(0, repetitions).SelectMany(_ => catalog.Queues.Single().ScriptIds).ToImmutableArray() }], [], []);
        Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), ExecutionSequentialFixture.Subject, changes),
            changes, DateTimeOffset.UtcNow);
        var finality = new ExecutionRunFinality(Source.Instance, Source.Database, new());
        var observer = new ActualObserver(new(Source.Instance, Source.Database, new(), Source.Lease, finality), Registration, receipt =>
        {
            if (indexedFixtureImage is null) return;
            var blobs = new ImmutableBlobStore(Source.Instance, new());
            var content = blobs.PersistContent(indexedFixtureImage, default);
            new RecordAssetStore(Source.Database, blobs).AttachVerified(new(receipt.Facts.RunId, content.Sha256,
                content.SizeBytes, NexusPipeline.Core.Records.RecordAssetKind.Screenshot));
        });
        var consumer = new ExecutionRuntimeConsumer(Source.Instance, Source.Database, new(), Source.Lease,
            _source.Base.Resources, _source.Base.Index, _source.Base.Inbox, terminalObserver ?? observer);
        _ = _source.Base.Service.Start(Guid.NewGuid(), ExecutionSequentialFixture.Subject,
            Source.Request(new QueueTarget(Source.QueueId)), DateTimeOffset.UtcNow);
        afterAccept?.Invoke();
        Receipt = Assert.IsType<AttemptReceipt>(await consumer.RunNextAsync());
        Assert.Null(consumer.FailureCode);
        if (!readSnapshot) return;
        using var connection = DatabaseConnections.Open(Source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.All);
        using var transaction = connection.BeginTransaction(deferred: true);
        var reference = NotificationReceiptRows.Find(connection, transaction, Receipt.Facts.AttemptId);
        Assert.NotNull(reference);
        Snapshot = new NotificationReceiptCodec(Source.Instance, Source.Database, new()).Read(reference, connection, transaction);
        transaction.Commit();
    }

    internal async Task Replace()
    {
        using var replacement = new SecretValue("https://notification-fixture.invalid/replacement");
        _ = await Catalog.SaveAsync(Guid.NewGuid(), "local:receipt-fixture", 1, TaskId, 1, Input,
            [new(NotificationSecretField.WebhookUrl, 1, SecretDisposition.Set, replacement)]);
    }

    internal void Sql(string sql)
    {
        using var connection = DatabaseConnections.Open(Source.Database.DatabasePath, domains: DatabaseDomain.All);
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    internal long Count(string table)
    {
        using var connection = DatabaseConnections.Open(Source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.All);
        using var command = connection.CreateCommand(); command.CommandText = $"SELECT count(*) FROM {DatabaseTables.Name(table)};";
        return (long)command.ExecuteScalar()!;
    }

    public void Dispose() => _source.Dispose();

    private sealed class ActualObserver(ExecutionRecordProjection records, ExecutionNotificationRegistration registration, Action<AttemptReceipt> publishIndexedFixtureAsset)
        : IAttemptTerminalObserver
    {
        public void OnCommitted(AttemptReceipt receipt) { records.OnCommitted(receipt); publishIndexedFixtureAsset(receipt); registration.OnCommitted(receipt); }
    }
}
