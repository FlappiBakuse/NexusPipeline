using System.Runtime.Versioning;
using NexusPipeline.Core.Records;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RetentionSettingsTests
{
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

    [Fact(DisplayName = "Pending Notification References Keep Expired History Visible And Prevent Content Retirement")]
    public async Task KeepsPendingNotifications()
    {
        using var fixture = new NotificationReceiptFixture(); await fixture.Execute(); var source = fixture.Source;
        var clock = new Clock { Now = DateTimeOffset.UtcNow.AddDays(8) };
        var service = new ContentRetirementService(source.Instance, source.Database, new(), source.Lease, fixture.Base.Resources, fixture.Base.Index);
        var retention = new RecordHistoryRetention(source.Database, service, clock); retention.Tick();
        Assert.Equal("record_retirement_notification_pending", retention.FailureCode);
        Assert.Single(new RunRecordStore(source.Database).ReadPage(new()).Items);
        Assert.Equal(0, fixture.Base.Count("record_content_retirements"));
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
