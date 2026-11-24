using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class NotificationRuntimeTests
{
    [Fact(DisplayName = "One Async Notification Pass Reuses Protected Catalog Bytes Across Acknowledgements And Releases Them")]
    public async Task ReusesPassCatalog()
    {
        var locks = new List<FileStream>(); string[] paths = []; var requests = 0;
        await using var sink = new NotificationHttpSink((_, _) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
                foreach (var path in paths)
                {
                    var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    locks.Add(file); file.Lock(0, file.Length);
                }
            return Task.FromResult(new NotificationSinkReply(200, ""));
        });
        await using var fixture = new RuntimeFixture();
        await fixture.Save(sink.Address);
        var accepted = fixture.Enqueue(); _ = fixture.Enqueue();
        var source = fixture.Base.Source;
        var snapshot = new NotificationDeliveryEvidence(source.Instance, source.Database, new()).Read(accepted.Delivery);
        var catalog = new NotificationCatalogCodec(source.Instance, new()).Read(snapshot.Document.Catalog);
        paths = catalog.Document.Tasks.Select(item => Path.Combine(source.Instance.RuntimeRoot, item.Definition.RelativePath))
            .Append(Path.Combine(source.Instance.RuntimeRoot, catalog.Reference.RelativePath)).ToArray();
        try
        {
            await fixture.Runtime.RunPassAsync();
            Assert.Equal(2, sink.Requests.Count);
            Assert.False(fixture.Runtime.HasPendingActivity);
            Assert.Null(fixture.Runtime.FailureCode);
        }
        finally
        {
            foreach (var file in locks) { file.Unlock(0, file.Length); file.Dispose(); }
        }
        var original = File.ReadAllBytes(paths[0]);
        try
        {
            File.WriteAllBytes(paths[0], [1]);
            Assert.ThrowsAny<Exception>(() => new NotificationCatalogCodec(source.Instance, new()).Read(catalog.Reference));
        }
        finally { File.WriteAllBytes(paths[0], original); }
    }

    [Fact(DisplayName = "An Idle Notification Worker Wakes For Each Committed Queue Registration Without Duplicating Delivery")]
    public async Task WakesForCommittedWork()
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        await using var fixture = new RuntimeFixture();
        fixture.Runtime.Start();
        await fixture.Save(sink.Address);
        for (var expected = 1; expected <= 2; expected++)
        {
            _ = fixture.Enqueue();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (sink.Requests.Count < expected || fixture.Runtime.HasPendingActivity)
                await Task.Delay(20, timeout.Token);
            Assert.Equal(expected, sink.Requests.Count);
            Assert.Null(fixture.Runtime.FailureCode);
        }
        Assert.True(fixture.Base.Index.Current.IsReady);
    }

    [Fact(DisplayName = "Startup Defers Missing Historical Registration While Keeping Queue Ownership And Exact Once Delivery")]
    public async Task BackfillsAfterStartup()
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        var input = new NotificationTaskInput("historical receipt", "", NotificationTaskKind.Webhook, true,
            NotificationChannelConfiguration.Default with { WebhookKind = NotificationWebhookKind.Generic,
                WebhookTemplate = "{\"text\":{text}}" });
        using var fixture = new NotificationReceiptFixture(input);
        var source = fixture.Source;
        using var url = new SecretValue(sink.Address.AbsoluteUri);
        await fixture.Execute(edits: [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url)],
            terminalObserver: new NexusPipeline.Platform.Storage.Execution.ExecutionRecordProjection(source.Instance,
                source.Database, new(), source.Lease, new(source.Instance, source.Database, new())), readSnapshot: false);
        await using var runtime = new NotificationRuntime(source.Instance, source.Database, new(), source.Lease,
            fixture.Base.Resources, fixture.Base.Index,
            new(source.Instance, source.Database, source.Lease, fixture.Base.Resources, fixture.Base.Index, new()));
        runtime.InitializeStartup();
        Assert.Equal(0, fixture.Count("notification_receipt_sources"));
        Assert.True(runtime.HasPendingActivity);
        Assert.Null(fixture.Base.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
        await runtime.RunPassAsync();
        Assert.Equal(1, fixture.Count("notification_receipt_sources"));
        Assert.False(runtime.HasPendingActivity);
        Assert.Single(sink.Requests);
        await runtime.RunPassAsync();
        Assert.Single(sink.Requests);
        Assert.True(fixture.Base.Index.Current.IsReady);
    }

    [Theory(DisplayName = "Actual Notification Runtime Protects Drain Until Acknowledgement And Keeps Unknown Pending")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainsPendingAcknowledgement(bool timeout)
    {
        await using var sink = new NotificationHttpSink(async (_, cancellation) =>
        {
            if (timeout) await Task.Delay(TimeSpan.FromSeconds(30), cancellation);
            return new(200, "");
        });
        await using var fixture = new RuntimeFixture();
        await fixture.Save(sink.Address);
        var accepted = fixture.Enqueue();
        var session = Guid.NewGuid();
        Assert.Null(fixture.Base.Lifecycle.RequestClose(session, Guid.NewGuid()).Permit);
        await fixture.Runtime.RunPassAsync();
        Assert.Single(sink.Requests);
        Assert.Equal(timeout, fixture.Runtime.HasPendingActivity);
        Assert.Equal(timeout, fixture.Base.Lifecycle.RequestClose(session, Guid.NewGuid()).Permit is null);
        await fixture.Runtime.RunPassAsync();
        Assert.Single(sink.Requests);
        Assert.Equal(0, fixture.Base.Count("attempt_receipts"));
        var snapshot = new NotificationDeliveryEvidence(fixture.Base.Source.Instance, fixture.Base.Source.Database, new()).Read(accepted.Delivery);
        Assert.Equal("savedTaskTest", snapshot.Document.Source.Kind);
    }

    [Fact(DisplayName = "Runtime Publishes An Owned Acknowledgement After Sql Recovery Without Resending")]
    public async Task ReplaysOwnedAcknowledgement()
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        await using var fixture = new RuntimeFixture();
        await fixture.Save(sink.Address); _ = fixture.Enqueue();
        fixture.Base.Execute("CREATE TRIGGER notifications.fail_runtime_notify_result BEFORE INSERT ON notification_delivery_transitions WHEN NEW.ordinal=2 BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        try
        {
            await fixture.Runtime.RunPassAsync();
            Assert.True(fixture.Runtime.HasPendingActivity);
            Assert.Single(sink.Requests);
            Assert.Null(fixture.Base.Resources.TryAcquire(Guid.NewGuid(), [new("secrets:fields", ResourceClaimMode.Mutation)]).Lease);
        }
        finally { fixture.Base.Execute("DROP TRIGGER notifications.fail_runtime_notify_result;"); }
        await fixture.Runtime.RunPassAsync();
        Assert.False(fixture.Runtime.HasPendingActivity); Assert.Single(sink.Requests);
        Assert.NotNull(fixture.Base.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
    }

    [Fact(DisplayName = "An Empty Tampered Pending Index Cannot Release Actual Notification Activity")]
    public async Task RejectsTamperedIndex()
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        await using var fixture = new RuntimeFixture();
        await fixture.Save(sink.Address); _ = fixture.Enqueue();
        fixture.Base.Execute("DELETE FROM notifications.notification_delivery_pending;");
        Assert.Throws<IOException>(() => fixture.Runtime.TryReleaseSettled());
        Assert.True(fixture.Runtime.HasPendingActivity); Assert.Empty(sink.Requests);
        Assert.Null(fixture.Base.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
    }

    [Fact(DisplayName = "Notification Activity Stays Held When A Waiting Row Is Restored Before The Release Snapshot")]
    public async Task KeepsWaitingWhenProjectionRecovers()
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        await using var fixture = new RuntimeFixture();
        await fixture.Save(sink.Address);
        _ = fixture.Enqueue();
        var source = fixture.Base.Source;
        source.Lease.BindDatabase(source.Database);
        var path = source.Database.DatabasePath;
        const DatabaseDomain domains = DatabaseDomain.Runtime | DatabaseDomain.Notifications;
        var saved = new object[3];
        SQLitePCL.sqlite3 handle;
        using (var connection = DatabaseConnections.Open(path, readOnly: true, domains: domains))
        using (var command = DatabaseConnections.Command(connection, null,
            "SELECT operation_id,source_operation_id,state FROM notifications.notification_delivery_pending;"))
        using (var row = command.ExecuteReader())
        {
            Assert.True(row.Read());
            Assert.Equal(3, row.GetValues(saved));
            Assert.False(row.Read());
        }
        fixture.Base.Execute("DELETE FROM notifications.notification_delivery_pending;");
        using (var connection = DatabaseConnections.Open(path)) handle = connection.Handle!;

        var restored = false;
        Exception? restoreError = null;
        SQLitePCL.strdelegate_trace restore = (_, sql) =>
        {
            if (restored || restoreError is not null || !sql.StartsWith("BEGIN", StringComparison.Ordinal)) return;
            try
            {
                using var writer = DatabaseConnections.Open(path, allowPooling: false, domains: domains);
                using var insert = DatabaseConnections.Command(writer, null,
                    "INSERT INTO notifications.notification_delivery_pending VALUES($0,$1,$2);", saved);
                restored = insert.ExecuteNonQuery() == 1;
            }
            catch (Exception error) { restoreError = error; }
        };
        SQLitePCL.raw.sqlite3_trace(handle, restore, null);
        bool released;
        try { released = fixture.Runtime.TryReleaseSettled(); }
        finally { SQLitePCL.raw.sqlite3_trace(handle, (SQLitePCL.strdelegate_trace?)null, null); }
        Assert.Null(restoreError);
        Assert.True(restored);
        Assert.False(released);
        Assert.True(fixture.Runtime.HasPendingActivity);
        Assert.Empty(sink.Requests);
        Assert.Null(fixture.Base.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
    }

    [Fact(DisplayName = "Startup Loads The Saved Test Queue Before Granting Close And Delivers Once")]
    public async Task LoadsStartupQueue()
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        await using var fixture = new RuntimeFixture();
        await fixture.Save(sink.Address); _ = fixture.Enqueue();
        await fixture.Runtime.DisposeAsync();
        fixture.Runtime = fixture.NewRuntime(); fixture.Runtime.InitializeStartup();
        Assert.True(fixture.Runtime.HasPendingActivity);
        var session = Guid.NewGuid();
        Assert.Null(fixture.Base.Lifecycle.RequestClose(session, Guid.NewGuid()).Permit);
        await fixture.Runtime.RunPassAsync();
        Assert.NotNull(fixture.Base.Lifecycle.RequestClose(session, Guid.NewGuid()).Permit);
        Assert.Single(sink.Requests);
    }

    [Fact(DisplayName = "An Actual Execution Can Register Its Notification After Drain Has Started")]
    public async Task RegistersDuringDrain()
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        var input = new NotificationTaskInput("runtime execution", "", NotificationTaskKind.Webhook, true,
            NotificationChannelConfiguration.Default with { WebhookKind = NotificationWebhookKind.Generic, WebhookTemplate = "{\"text\":{text}}" });
        using var fixture = new NotificationReceiptFixture(input);
        var source = fixture.Source;
        await using var runtime = new NotificationRuntime(source.Instance, source.Database, new(), source.Lease,
            fixture.Base.Resources, fixture.Base.Index,
            new(source.Instance, source.Database, source.Lease, fixture.Base.Resources, fixture.Base.Index, new()));
        runtime.InitializeStartup();
        var session = Guid.NewGuid();
        using var url = new SecretValue(sink.Address.AbsoluteUri);
        await fixture.Execute(edits: [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url)],
            terminalObserver: runtime, afterAccept: () => Assert.Null(fixture.Base.Lifecycle.RequestClose(session, Guid.NewGuid()).Permit));
        Assert.True(runtime.HasPendingActivity);
        Assert.Null(fixture.Base.Lifecycle.RequestClose(session, Guid.NewGuid()).Permit);
        await runtime.RunPassAsync();
        Assert.Single(sink.Requests); Assert.False(runtime.HasPendingActivity);
        Assert.NotNull(fixture.Base.Lifecycle.RequestClose(session, Guid.NewGuid()).Permit);
        Assert.Equal(1, fixture.Count("run_records")); Assert.Equal(1, fixture.Count("attempt_receipts"));
    }

    [Fact(DisplayName = "A Registration Epoch Change Prevents A Concurrent Empty Release")]
    public void RechecksRegistrationEpoch()
    {
        using var fixture = new ExecutionAdmissionFixture();
        using var owner = new QueueActivityOwner(fixture.Resources);
        using (var root = fixture.Resources.TryAcquire(Guid.NewGuid(), [new("notifications:queue", ResourceClaimMode.Mutation)]).Lease)
        { Assert.NotNull(root); owner.Ensure(root); }
        var epoch = Assert.IsType<long>(owner.ReadReleaseEpoch());
        using (owner.BeginRegistration())
        { Assert.Null(owner.ReadReleaseEpoch()); Assert.False(owner.TryRelease(epoch)); }
        Assert.False(owner.TryRelease(epoch)); Assert.True(owner.IsActive);
        Assert.True(owner.TryRelease(Assert.IsType<long>(owner.ReadReleaseEpoch())));
        Assert.False(owner.IsActive);
    }

    [Fact(DisplayName = "Settling And Restarting Keep Ended Notifications Unread While Explicit Diagnosis Still Validates Them")]
    public async Task SettlesCurrentActivity()
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        await using var fixture = new RuntimeFixture();
        await fixture.Save(sink.Address); var first = fixture.Enqueue();
        await fixture.Runtime.RunPassAsync();
        Assert.False(fixture.Runtime.HasPendingActivity); Assert.Single(sink.Requests);
        using var historical = new FileStream(Path.Combine(fixture.Base.Source.Instance.RuntimeRoot, first.Delivery.RelativePath),
            FileMode.Open, FileAccess.Read, FileShare.None);
        _ = fixture.Enqueue(); await fixture.Runtime.RunPassAsync();
        Assert.False(fixture.Runtime.HasPendingActivity); Assert.Equal(2, sink.Requests.Count);
        await using var restart = fixture.NewRuntime();
        restart.InitializeStartup();
        Assert.False(restart.HasPendingActivity);
        var source = fixture.Base.Source;
        Assert.Throws<IOException>(() => DeliveryReferenceIndex.VerifyReadOnly(source.Instance, source.Database, new()));
        Assert.Equal(2, sink.Requests.Count);
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        internal ExecutionAdmissionFixture Base { get; } = new();
        internal NotificationRuntime Runtime { get; set; }
        private readonly Guid _task = Guid.NewGuid();
        internal RuntimeFixture() { Runtime = NewRuntime(); Runtime.InitializeStartup(); }
        internal NotificationRuntime NewRuntime()
        {
            var source = Base.Source;
            return new(source.Instance, source.Database, new(), source.Lease, Base.Resources, Base.Index,
                new(source.Instance, source.Database, source.Lease, Base.Resources, Base.Index, new()));
        }
        internal async Task Save(Uri address)
        {
            var source = Base.Source;
            var catalog = new NotificationCatalogService(source.Instance, source.Database, source.Lease, Base.Resources, Base.Index, new());
            using var url = new SecretValue(address.AbsoluteUri);
            _ = await catalog.SaveAsync(Guid.NewGuid(), "local:runtime-fixture", 0, _task, 0,
                new("runtime task", "", NotificationTaskKind.Webhook, true, NotificationChannelConfiguration.Default with {
                    TimeoutSeconds = 1, WebhookKind = NotificationWebhookKind.Generic, WebhookTemplate = "{\"text\":{text}}" }),
                [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url)]);
        }
        internal TaskTestAcceptance Enqueue()
        {
            var operation = Guid.NewGuid();
            using var root = Base.Resources.TryAcquire(operation, TaskTestService.AdmissionClaims).Lease;
            Assert.NotNull(root);
            return Runtime.EnqueueSavedTest(root, operation, "local:runtime-fixture", 1, _task, 1);
        }
        public async ValueTask DisposeAsync() { await Runtime.DisposeAsync(); Base.Dispose(); }
    }
}
