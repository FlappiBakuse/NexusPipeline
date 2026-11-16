using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RetentionSettingsTests
{
    [Fact(DisplayName = "Unknown Delivery Keeps Its Own Run While Unrelated Expired History Still Retires")]
    public async Task RetiresBesideUnknownDelivery()
    {
        using var fixture = new NotificationReceiptFixture(); await fixture.Execute();
        var source = fixture.Source;
        Assert.Equal(1, fixture.Delivery.MaterializePending());
        var reference = Assert.Single(new NotificationRuntimeQueue(source.Instance, source.Database, new()).Read("queued")).Reference;
        var delivery = new NotificationDeliveryEvidence(source.Instance, source.Database, new()).Read(reference);
        NotificationDeliveryNetwork network;
        using (var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true,
            domains: DatabaseDomain.Runtime | DatabaseDomain.System))
        using (var transaction = connection.BeginTransaction(deferred: true))
            network = DeliveryNetworkEvidence.CaptureCurrent(connection, transaction, source.Instance.InstanceId);
        var sending = new NotificationSendingStore(source.Instance, source.Database, new(), source.Lease);
        var claimed = sending.Claim(delivery, network);
        sending.Publish(delivery, claimed, new(NotificationTransportOutcome.Unknown, null));
        Assert.Equal(1, fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId).Unknown);
        var original = new JournalStore(source.Instance, new()).Read(fixture.Receipt.JournalId).Export();
        var catalog = source.Definitions.Read();
        var binding = catalog.Bindings.Single(item => item.Id == source.OtherBinding);
        var change = new AutomationChanges(catalog.Revision, [], [], [],
            [binding with { NotifyEnabled = false, NotificationTaskIds = [] }], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), ExecutionSequentialFixture.Subject, change),
            change, DateTimeOffset.UtcNow);
        var projection = new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
            new(source.Instance, source.Database, new()));
        var consumer = new ExecutionRuntimeConsumer(source.Instance, source.Database, new(), source.Lease,
            fixture.Base.Resources, fixture.Base.Index, fixture.Base.Inbox, projection);
        _ = fixture.Base.Service.Start(Guid.NewGuid(), ExecutionSequentialFixture.Subject,
            source.Request(new BindingTarget(source.OtherBinding)), DateTimeOffset.UtcNow);
        var unrelated = Assert.IsType<AttemptReceipt>(await consumer.RunNextAsync());
        var saved = source.Settings.Read();
        var retentionChange = new HostSettingsChange(saved.Revision, saved.Values with { HistoryRetentionDays = 1 });
        source.Settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "retention-fixture", retentionChange), retentionChange, DateTimeOffset.UtcNow);
        var service = new ContentRetirementService(source.Instance, source.Database, new(), source.Lease, fixture.Base.Resources, fixture.Base.Index, source.PayloadRoots);
        var retention = new RecordHistoryRetention(source.Database, service, new Clock { Now = DateTimeOffset.UtcNow.AddDays(2) });
        var history = new AttemptHistoryReader(source.Database);
        for (var index = 0; index < 12 && history.ReadPage(new(unrelated.Facts.RunId)).Items.Count != 0; index++) retention.Tick();
        Assert.Empty(history.ReadPage(new(unrelated.Facts.RunId)).Items);
        Assert.Equal(fixture.Receipt.Facts.RunId, Assert.Single(new RunRecordStore(source.Database).ReadPage(new()).Items).FinalAttempt.RunId);
        Assert.Equal(1, fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId).Unknown);
        Assert.Equal(original, new JournalStore(source.Instance, new()).Read(fixture.Receipt.JournalId).Export());
        Assert.Equal(fixture.Receipt, new AttemptReceiptStore(source.Database).Read(fixture.Receipt.Facts.AttemptId));
    }

    [Fact(DisplayName = "Automatic Retention Leaves Accepted Execution Untouched And Resumes The Same Expired Run When Idle")]
    public async Task DefersAcceptedExecution()
    {
        using var fixture = await RecordRetirementFixture.CreateAsync(); var source = fixture.Source;
        var saved = source.Settings.Read();
        var change = new HostSettingsChange(saved.Revision, saved.Values with { HistoryRetentionDays = 1 });
        source.Settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "retention-fixture", change), change, DateTimeOffset.UtcNow);
        _ = fixture.Hooks.Base.Service.Start(Guid.NewGuid(), ExecutionHooksFixture.Subject,
            source.Request(new BindingTarget(source.BindingA)), DateTimeOffset.UtcNow);
        var retention = new RecordHistoryRetention(source.Database, fixture.Retirement,
            new Clock { Now = DateTimeOffset.UtcNow.AddDays(2) });
        var before = source.Snapshot(); var sequence = source.Database.CommitSequence;
        retention.Tick(); retention.Tick();
        Assert.Null(retention.FailureCode);
        var after = source.Snapshot();
        Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        Assert.Equal(sequence, source.Database.CommitSequence);
        Assert.NotNull(await fixture.Hooks.Consumer.RunNextAsync());
        var records = new RunRecordStore(source.Database);
        for (var index = 0; index < 12 && records.ReadPage(new()).Items.Count != 0; index++) retention.Tick();
        Assert.Null(retention.FailureCode); Assert.Empty(records.ReadPage(new()).Items);
        Assert.Equal(fixture.Receipt, new AttemptReceiptStore(source.Database).Read(fixture.Receipt.Facts.AttemptId));
        Assert.Equal(1, fixture.Hooks.Base.Count("run_records"));
    }

    [Fact(DisplayName = "Saved Retention Days Expire Actual History And Raw Content Without Removing Recovery Facts")]
    public async Task ExpiresDisplayContent()
    {
        using var fixture = await RecordRetirementFixture.CreateAsync(); var source = fixture.Source;
        var clock = new Clock { Now = DateTimeOffset.UtcNow.AddDays(2) };
        var retention = new RecordHistoryRetention(source.Database, fixture.Retirement, clock);
        var records = new RunRecordStore(source.Database);
        retention.Tick(); Assert.Single(records.ReadPage(new()).Items);
        Assert.Equal(0, fixture.Hooks.Base.Count("record_content_retirements"));
        var before = source.Settings.Read(); var change = new HostSettingsChange(before.Revision, before.Values with { HistoryRetentionDays = 1 });
        source.Settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "retention-fixture", change), change, DateTimeOffset.UtcNow);
        for (var index = 0; index < 12 && records.ReadPage(new()).Items.Count != 0; index++) retention.Tick();
        Assert.Null(retention.FailureCode); Assert.Empty(records.ReadPage(new()).Items);
        Assert.Empty(new AttemptHistoryReader(source.Database).ReadPage(new(fixture.Receipt.Facts.RunId)).Items);
        Assert.Equal(fixture.Receipt, new AttemptReceiptStore(source.Database).Read(fixture.Receipt.Facts.AttemptId));
        Assert.Equal(1, fixture.Hooks.Base.Count("run_records"));
        Assert.All(fixture.Items, item => Assert.Equal(RecordContentAvailability.Retired, item.ContentAvailability));
        var commits = source.Database.CommitSequence; retention.Tick(); retention.Tick();
        Assert.Equal(commits, source.Database.CommitSequence);
    }

    [Fact(DisplayName = "Pending Notifications Defer Automatic Retention Without Changing Expired History")]
    public async Task KeepsPendingNotifications()
    {
        using var fixture = new NotificationReceiptFixture(); await fixture.Execute(); var source = fixture.Source;
        var clock = new Clock { Now = DateTimeOffset.UtcNow.AddDays(8) };
        var service = new ContentRetirementService(source.Instance, source.Database, new(), source.Lease, fixture.Base.Resources, fixture.Base.Index, source.PayloadRoots);
        var retention = new RecordHistoryRetention(source.Database, service, clock);
        var before = source.Snapshot(); retention.Tick();
        Assert.Null(retention.FailureCode);
        var after = source.Snapshot();
        Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        Assert.Single(new RunRecordStore(source.Database).ReadPage(new()).Items);
        Assert.Equal(0, fixture.Base.Count("record_content_retirements"));
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
