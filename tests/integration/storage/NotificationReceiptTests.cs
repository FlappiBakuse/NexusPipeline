using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class NotificationReceiptTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "Pending Notification Reads Reuse Protected Catalog Bytes And The Held Instance Identity")]
    public async Task ReusesPendingCatalog()
    {
        using var fixture = new NotificationReceiptFixture();
        await fixture.Execute(taskCount: 2, queueNotifications: false, repetitions: 3);
        Assert.Equal(3, fixture.Delivery.MaterializePending());
        var source = fixture.Source;
        string[] paths;
        using (var scope = BlobReadScope.Hold(source.Instance))
        {
            var catalog = new NotificationCatalogCodec(source.Instance, new()).Read(fixture.Snapshot.Document.Catalog);
            paths = catalog.Document.Tasks.Select(item => Path.Combine(source.Instance.RuntimeRoot, item.Definition.RelativePath))
                .Append(Path.Combine(source.Instance.RuntimeRoot, catalog.Reference.RelativePath)).ToArray();
            Assert.Equal(6, fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId).Queued);
            var locks = new List<FileStream>();
            try
            {
                foreach (var path in paths.Append(Path.Combine(source.Instance.RuntimeRoot, "instance.json")))
                {
                    var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    locks.Add(file); file.Lock(0, file.Length);
                }
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var clock = Stopwatch.StartNew();
                for (var index = 0; index < 8; index++)
                {
                    var status = fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId);
                    Assert.True(status.IsExpanded); Assert.Equal(6, status.Queued);
                }
                var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                output.WriteLine("Protected pending reads: count=8, routes=3, targets=6, lockedCatalogFiles={0}, allocatedBytes={1}, elapsedMilliseconds={2:F3}",
                    paths.Length, allocatedBytes, clock.Elapsed.TotalMilliseconds);
                fixture.Sql("UPDATE notifications.notification_delivery_secrets SET revision=revision+1;");
                try { Assert.ThrowsAny<Exception>(() => fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId)); }
                finally { fixture.Sql("UPDATE notifications.notification_delivery_secrets SET revision=revision-1;"); }
            }
            finally
            {
                foreach (var file in locks) { file.Unlock(0, file.Length); file.Dispose(); }
            }
        }
        var original = File.ReadAllBytes(paths[0]);
        File.WriteAllBytes(paths[0], [9]);
        try { Assert.Throws<IOException>(() => fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId)); }
        finally { File.WriteAllBytes(paths[0], original); }
        Assert.Equal(6, fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId).Queued);
    }

    [Fact(DisplayName = "Repeated Protected Receipt Reads Reuse Terminal Decoding And Send Each Frozen Route Once")]
    public async Task ReusesTerminalDecoding()
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        var input = new NexusPipeline.Core.Notifications.NotificationTaskInput("receipt decoding", "",
            NexusPipeline.Core.Notifications.NotificationTaskKind.Webhook, true,
            NexusPipeline.Core.Notifications.NotificationChannelConfiguration.Default with
            { WebhookKind = NexusPipeline.Core.Notifications.NotificationWebhookKind.Generic,
                WebhookTemplate = "{\"text\":{text}}" });
        using var fixture = new NotificationReceiptFixture(input);
        using var url = new SecretValue(sink.Address.AbsoluteUri);
        await fixture.Execute(queueNotifications: false, repetitions: 3,
            edits: [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url)]);
        var source = fixture.Source;
        var original = new JournalStore(source.Instance, new()).Read(fixture.Receipt.JournalId).Export();
        var contents = Directory.EnumerateFiles(Path.Combine(source.Instance.RuntimeRoot, "blobs", "content"))
            .ToDictionary(path => path, File.ReadAllBytes);
        using var reads = new NotificationAttemptReads(source.Instance, source.Database, new(), source.Lease);
        reads.HoldReceipt(fixture.Snapshot.Reference);
        var first = ReadReceipt(fixture); var last = first;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime; var clock = Stopwatch.StartNew();
        for (var index = 0; index < 8; index++) last = ReadReceipt(fixture);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine("Repeated receipt reads: count=8, allocatedBytes={0}, processCpuMilliseconds={1:F3}, elapsedMilliseconds={2:F3}",
            allocatedBytes, (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds,
            clock.Elapsed.TotalMilliseconds);
        Assert.InRange(allocatedBytes, 0L, 24L * 1024 * 1024);
        Assert.True(first.Snapshot.Document.OrderedFacts.SequenceEqual(last.Snapshot.Document.OrderedFacts));
        Assert.Equal(3, last.Routes.Length);
        for (var ordinal = 0; ordinal < first.Routes.Length; ordinal++)
            Assert.Equal(NotificationReceiptText.Render(first, ordinal), NotificationReceiptText.Render(last, ordinal));
        await using var runtime = new NotificationRuntime(source.Instance, source.Database, new(), source.Lease,
            fixture.Base.Resources, fixture.Base.Index,
            new(source.Instance, source.Database, source.Lease, fixture.Base.Resources, fixture.Base.Index, new()));
        runtime.InitializeStartup(); await runtime.RunPassAsync();
        Assert.False(runtime.HasPendingActivity); Assert.Equal(3, sink.Requests.Count);
        await runtime.RunPassAsync(); Assert.Equal(3, sink.Requests.Count);
        Assert.Equal(original, new JournalStore(source.Instance, new()).Read(fixture.Receipt.JournalId).Export());
        foreach (var content in contents) Assert.Equal(content.Value, File.ReadAllBytes(content.Key));
        reads.Dispose();
        var directory = Path.Combine(source.Instance.RuntimeRoot, "journals", fixture.Receipt.JournalId.ToString("D"));
        using var writable = new FileStream(Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal).Last(),
            FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        Assert.Equal(original.Length, writable.Length);
    }

    [Fact(DisplayName = "Cached Receipt Decoding Rechecks Sql And Rejects Changed Journal Bytes After Its Owner Releases")]
    public async Task RechecksReleasedReceiptSources()
    {
        using var fixture = new NotificationReceiptFixture(); await fixture.Execute(repetitions: 3);
        var source = fixture.Source;
        using (var reads = new NotificationAttemptReads(source.Instance, source.Database, new(), source.Lease))
        {
            reads.HoldReceipt(fixture.Snapshot.Reference); _ = ReadReceipt(fixture);
            fixture.Sql("UPDATE notifications.notification_receipt_sources SET route_count=route_count+1;");
            try { Assert.Throws<IOException>(() => ReadReceipt(fixture)); }
            finally { fixture.Sql("UPDATE notifications.notification_receipt_sources SET route_count=route_count-1;"); }
            _ = ReadReceipt(fixture);
        }
        var directory = Path.Combine(source.Instance.RuntimeRoot, "journals", fixture.Receipt.JournalId.ToString("D"));
        var path = Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal).Last();
        var original = File.ReadAllBytes(path); File.WriteAllBytes(path, [9]);
        try
        {
            var before = source.Snapshot();
            Assert.Throws<System.Text.Json.JsonException>(() => ReadReceipt(fixture));
            var after = source.Snapshot();
            Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        }
        finally { File.WriteAllBytes(path, original); }
        _ = ReadReceipt(fixture);
    }

    [Fact(DisplayName = "Terminal Notification Batches Reuse Protected Evidence And Recheck Current Source Bindings")]
    public async Task ReusesTerminalEvidence()
    {
        using var fixture = new NotificationReceiptFixture();
        await fixture.Execute(taskCount: 2, queueNotifications: false, repetitions: 3);
        var source = fixture.Source;
        using var reads = new NotificationAttemptReads(source.Instance, source.Database, new(), source.Lease);
        reads.HoldReceipt(fixture.Snapshot.Reference);
        var registered = new RecoveryJournalRegistry(source.Database).ReadJournal(fixture.Receipt.JournalId);
        var metadata = AttemptPreparedBinding.ReadPlanMetadata(source.Instance, new(), registered);
        var planPath = Path.Combine(source.Instance.RuntimeRoot, registered.PlanPath);
        var paths = Directory.EnumerateFiles(Path.Combine(source.Instance.RuntimeRoot, "journals", registered.JournalId.ToString("D")))
            .Append(planPath).Append(Path.Combine(source.Instance.RuntimeRoot, metadata.Plan.ExecutionPlan.RelativePath)).ToArray();
        var locks = new List<FileStream>();
        try
        {
            foreach (var path in paths)
            {
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                locks.Add(file); file.Lock(0, file.Length);
            }
            Assert.Throws<IOException>(() => new JournalStore(source.Instance, new()).Read(registered.JournalId));
            new LogCatalogProjection(source.Instance, source.Database, new(), source.Lease).OnCommitted(fixture.Receipt);
            Assert.Equal(3, fixture.Delivery.MaterializePending(64, reads));
            var status = fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId);
            Assert.True(status.IsExpanded); Assert.Equal(6, status.Queued);
            fixture.Sql("UPDATE notifications.notification_receipt_sources SET route_count=route_count+1;");
            try { Assert.ThrowsAny<Exception>(() => fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId)); }
            finally { fixture.Sql("UPDATE notifications.notification_receipt_sources SET route_count=route_count-1;"); }
            Assert.Throws<IOException>(() => new FileStream(planPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read).Dispose());
        }
        finally
        {
            foreach (var file in locks) { file.Unlock(0, file.Length); file.Dispose(); }
        }
        reads.Dispose();
        using var released = new FileStream(planPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        Assert.Equal(6, fixture.Count("notification_deliveries"));
    }

    [Theory(DisplayName = "Three Queue Occurrences Publish One Notification Route Per Binding And Remain Startup Reconcilable")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoutesRepeatedBindings(bool queueNotifications)
    {
        using var fixture = new NotificationReceiptFixture();
        await fixture.Execute(queueNotifications: queueNotifications, repetitions: 3);
        var report = new NexusPipeline.Platform.Storage.Execution.ExecutionLogReader(fixture.Source.Instance,
            fixture.Source.Database, new()).ReadReport(fixture.Receipt.Facts.RunId, fixture.Receipt.Facts.AttemptId);
        Assert.Equal(9, report.Steps.Count);
        Assert.All(report.Steps, step => Assert.Equal("collected", step.Phase));
        Assert.Equal(queueNotifications ? 1 : 3, fixture.Delivery.MaterializePending());
        Assert.Equal(queueNotifications ? 1 : 3, fixture.Count("notification_deliveries"));
        fixture.Registration.ReconcilePending();
        fixture.Registration.OnCommitted(fixture.Receipt);
        Assert.Equal(1, fixture.Count("notification_receipt_sources"));
        Assert.True(fixture.Base.Index.Current.IsReady);
    }

    [Fact(DisplayName = "Each Multi-Target Route Commits Its Exact Prefix And Rolls Back Changed References")]
    public async Task MaterializesMultiTargetPrefixes()
    {
        using var fixture = new NotificationReceiptFixture();
        await fixture.Execute(taskCount: 10, queueNotifications: false, repetitions: 3);
        var run = fixture.Receipt.Facts.RunId;
        Assert.Equal(3, fixture.Snapshot.Document.RouteCount);

        Assert.Equal(1, fixture.Delivery.MaterializePending(1));
        Assert.Equal(1, fixture.Count("notification_receipt_routes"));
        Assert.Equal(10, fixture.Count("notification_deliveries"));
        var first = fixture.Delivery.ReadPendingForRun(run);
        Assert.False(first.IsExpanded);
        Assert.Equal(10, first.Queued);

        var firstRoute = PrepareRoute(fixture, 0);
        Publish(0);
        Assert.Equal(first, fixture.Delivery.ReadPendingForRun(run));
        Assert.ThrowsAny<IOException>(() => Publish(0, (connection, transaction) =>
        {
            using var command = NotificationDeliveryRows.Command(connection, transaction,
                "DELETE FROM notifications.notification_receipt_deliveries WHERE delivery_operation_id=$0;",
                firstRoute.Deliveries[0].Document.OperationId.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }));
        Assert.ThrowsAny<IOException>(() => Publish(2));
        Assert.Equal(first, fixture.Delivery.ReadPendingForRun(run));

        var selected = fixture.Snapshot.Document.Tasks[0];
        var other = fixture.Snapshot.Document.Tasks[1];
        var original = Assert.Single(selected.Secrets.Where(field => field.Payload is not null)).Payload!;
        var alien = Assert.Single(other.Secrets.Where(field => field.Payload is not null)).Payload!;
        Assert.NotEqual(original.SecretId, alien.SecretId);
        fixture.Sql("CREATE TRIGGER notifications.damage_route_refs AFTER INSERT ON notification_receipt_routes " +
            "WHEN NEW.ordinal=1 BEGIN UPDATE notification_receipt_secrets SET secret_id='" + alien.SecretId.ToString("D") +
            "' WHERE operation_id=NEW.operation_id AND task_id='" + selected.TaskId.ToString("D") + "' AND field='webhook-url'; END;");
        try { Assert.ThrowsAny<IOException>(() => fixture.Delivery.MaterializePending(1)); }
        finally { fixture.Sql("DROP TRIGGER notifications.damage_route_refs;"); }
        Assert.Equal(1, fixture.Count("notification_receipt_routes"));
        Assert.Equal(10, fixture.Count("notification_deliveries"));
        Assert.Equal(first, fixture.Delivery.ReadPendingForRun(run));

        Assert.Equal(1, fixture.Delivery.MaterializePending(1));
        Assert.Equal(2, fixture.Count("notification_receipt_routes"));
        Assert.Equal(20, fixture.Delivery.ReadPendingForRun(run).Queued);
        Assert.Equal(1, fixture.Delivery.MaterializePending(1));
        fixture.Registration.OnCommitted(fixture.Receipt);
        Assert.Equal(0, fixture.Delivery.MaterializePending(1));
        var saved = fixture.Delivery.ReadPendingForRun(run);
        Assert.True(saved.IsExpanded);
        Assert.Equal(30, saved.Queued);
        Assert.Equal(30, fixture.Count("notification_deliveries"));

        void Publish(int ordinal, Action<SqliteConnection, SqliteTransaction>? change = null)
        {
            var route = PrepareRoute(fixture, ordinal);
            fixture.Source.Database.Write((connection, transaction) =>
            {
                change?.Invoke(connection, transaction);
                ReceiptRouteRows.PublishInside(connection, transaction, fixture.Snapshot, route.Document, route.Reference,
                    route.Deliveries, DateTimeOffset.UtcNow, fixture.Source.Lease.StartupGeneration);
                return true;
            });
        }
    }

    [Fact(DisplayName = "Materializing A Partly Accepted Route Preserves The Existing Delivery And Fills Its Membership")]
    public async Task ReusesPartlyAcceptedRoute()
    {
        using var fixture = new NotificationReceiptFixture();
        await fixture.Execute(taskCount: 10);
        var source = fixture.Source;
        var route = PrepareRoute(fixture, 0);
        var prior = route.Deliveries[0];
        var id = prior.Document.OperationId;
        var before = source.Database.Write((connection, transaction) =>
        {
            NotificationDeliveryRows.AcceptRegisteredInside(connection, transaction, prior, fixture.Snapshot, DateTimeOffset.UtcNow, source.Lease.StartupGeneration);
            return NotificationDeliveryRows.ReadState(connection, transaction, id);
        });
        Assert.Equal(1, fixture.Count("notification_deliveries"));
        Assert.Equal(0, fixture.Count("notification_receipt_deliveries"));

        Assert.Equal(1, fixture.Delivery.MaterializePending(1));
        Assert.Equal(10, fixture.Count("notification_deliveries"));
        Assert.Equal(10, fixture.Count("notification_receipt_deliveries"));
        var status = fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId);
        Assert.True(status.IsExpanded);
        Assert.Equal(10, status.Queued);
        using var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications);
        using var transaction = DatabaseConnections.BeginRead(connection);
        NotificationDeliveryRows.RequireSaved(connection, transaction, prior);
        ReceiptRouteRows.RequireMembership(connection, transaction, prior);
        Assert.Equal(before, NotificationDeliveryRows.ReadState(connection, transaction, id));
        transaction.Commit();
    }

    private static (ReceiptRouteDocument Document, BlobReference Reference, IReadOnlyList<NotificationDeliverySnapshot> Deliveries)
        PrepareRoute(NotificationReceiptFixture fixture, int ordinal)
    {
        var source = fixture.Source;
        ReceiptReadContext context;
        using (var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.All))
        using (var transaction = DatabaseConnections.BeginRead(connection))
        {
            context = new NotificationReceiptCodec(source.Instance, source.Database, new())
                .ReadContext(fixture.Snapshot.Reference, connection, transaction);
            transaction.Commit();
        }
        var result = new ReceiptRouteCodec(source.Instance, source.Database, new()).Prepare(context, ordinal);
        return (result.Document, result.Reference, result.Deliveries);
    }

    [Theory(DisplayName = "Actual Delivery Uses The Saved Receipt Locale Even If Settings Change Before Fanout")]
    [InlineData(NexusPipeline.Core.Settings.HostLocale.ZhCn)]
    [InlineData(NexusPipeline.Core.Settings.HostLocale.EnUs)]
    public async Task KeepsSavedLocale(NexusPipeline.Core.Settings.HostLocale locale)
    {
        using var fixture = new NotificationReceiptFixture();
        Save(locale); await fixture.Execute();
        Save(locale == NexusPipeline.Core.Settings.HostLocale.ZhCn ? NexusPipeline.Core.Settings.HostLocale.EnUs : NexusPipeline.Core.Settings.HostLocale.ZhCn);
        Assert.Equal(1, fixture.Delivery.MaterializePending());
        using var connection = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Notifications);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT relative_path,size_bytes,sha256 FROM notifications.notification_deliveries;";
        using var row = command.ExecuteReader(); Assert.True(row.Read());
        var delivery = new NotificationDeliveryCodec(fixture.Source.Instance, new()).Read(new(row.GetString(0), row.GetInt64(1), row.GetString(2))).Document;
        Assert.Contains(locale == NexusPipeline.Core.Settings.HostLocale.ZhCn ? "执行：" : "Execution:", delivery.Body);
        Assert.Contains("Engine=", delivery.Body); Assert.Contains("Reason=", delivery.Body);
        Assert.Equal(locale, fixture.Snapshot.Document.Locale);

        void Save(NexusPipeline.Core.Settings.HostLocale value)
        {
            var saved = fixture.Source.Settings.Read();
            var change = new NexusPipeline.Core.Settings.HostSettingsChange(saved.Revision, saved.Values with { Locale = value });
            fixture.Source.Settings.Commit(NexusPipeline.Platform.Storage.Sqlite.HostSettingsStore.Identity(Guid.NewGuid(), "locale-fixture", change), change, DateTimeOffset.UtcNow);
        }
    }

    [Fact(DisplayName = "An Actual Receipt Pins Its Frozen Credential Across Replacement And Route Publication")]
    public async Task PinsFrozenCredential()
    {
        using var fixture = new NotificationReceiptFixture(); await fixture.Execute();
        var source = fixture.Snapshot.Document;
        var original = Assert.Single(Assert.Single(source.Tasks).Secrets.Where(item => item.Payload is not null)).Payload!;
        Assert.False(fixture.Delivery.IsSettledForRun(fixture.Receipt.Facts.RunId));
        await fixture.Replace();
        Assert.True(DeliveryReferenceIndex.IsSecretReferenced(fixture.Source.Instance, fixture.Source.Database,
            new(), original.SecretId, original.Digest, original.SizeBytes));
        Assert.Equal(1, fixture.Delivery.MaterializePending());
        Assert.Equal(0, fixture.Delivery.MaterializePending());
        fixture.Registration.OnCommitted(fixture.Receipt);
        Assert.Equal(1, fixture.Count("notification_receipt_sources"));
        Assert.Equal(1, fixture.Count("notification_deliveries"));
        var status = fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId);
        Assert.True(status.IsRegistered); Assert.True(status.IsExpanded); Assert.Equal(1, status.Queued); Assert.False(status.IsSettled);
        Assert.True(DeliveryReferenceIndex.IsSecretReferenced(fixture.Source.Instance, fixture.Source.Database,
            new(), original.SecretId, original.Digest, original.SizeBytes));
        Assert.Null(source.Screenshot);
    }

    [Fact(DisplayName = "An Enabled Empty Queue Publishes An Empty Authoritative Route Without Binding Fallback")]
    public async Task PublishesEmptyRoutes()
    {
        using var fixture = new NotificationReceiptFixture(); await fixture.Execute(emptyQueue: true);
        Assert.Empty(fixture.Snapshot.Document.Tasks);
        Assert.Equal(1, fixture.Delivery.MaterializePending());
        Assert.Equal(0, fixture.Count("notification_deliveries"));
        var status = fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId);
        Assert.True(status.IsRegistered); Assert.True(status.IsExpanded); Assert.True(status.IsSettled);
        Assert.Equal(0, status.Queued);
    }

    [Theory(DisplayName = "A Route Sql Failure Preserves The Source Pin And Retries The Same Immutable Delivery")]
    [InlineData("notification_receipt_routes")]
    [InlineData("notification_deliveries")]
    public async Task ReplaysFailedRoutes(string table)
    {
        using var fixture = new NotificationReceiptFixture(); await fixture.Execute();
        fixture.Sql($"CREATE TRIGGER notifications.fail_route BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        Assert.Throws<SqliteException>(() => fixture.Delivery.MaterializePending());
        Assert.Equal(0, fixture.Count("notification_receipt_routes")); Assert.Equal(0, fixture.Count("notification_deliveries"));
        Assert.Equal(1, fixture.Count("notification_receipt_sources"));
        var payload = Assert.Single(Assert.Single(fixture.Snapshot.Document.Tasks).Secrets.Where(item => item.Payload is not null)).Payload!;
        Assert.True(DeliveryReferenceIndex.IsSecretReferenced(fixture.Source.Instance, fixture.Source.Database,
            new(), payload.SecretId, payload.Digest, payload.SizeBytes));
        fixture.Sql("DROP TRIGGER notifications.fail_route;");
        Assert.Equal(1, fixture.Delivery.MaterializePending()); Assert.Equal(0, fixture.Delivery.MaterializePending());
        Assert.Equal(1, fixture.Count("notification_deliveries"));
    }

    [Theory(DisplayName = "Verified Source And Delivery Retain An Old Credential Across Materialization")]
    [InlineData(1)]
    [InlineData(10)]
    public async Task ReadsCoherentHandoff(int taskCount)
    {
        using var fixture = new NotificationReceiptFixture();
        await fixture.Execute(taskCount: taskCount);
        await fixture.Replace();
        var payload = Assert.Single(fixture.Snapshot.Document.Tasks.Single(task => task.TaskId == fixture.TaskId)
            .Secrets.Where(item => item.Payload is not null)).Payload!;
        var instance = fixture.Source.Instance;
        var database = fixture.Source.Database;
        var run = fixture.Receipt.Facts.RunId;
        var source = fixture.Delivery.ReadPendingForRun(run);
        Assert.True(source.IsRegistered);
        Assert.False(source.IsExpanded);
        Assert.Equal(0, fixture.Count("notification_deliveries"));
        DeliveryReferenceIndex.VerifyReadOnly(instance, database, new());
        Assert.True(DeliveryReferenceIndex.IsSecretReferenced(instance, database, new(), payload.SecretId, payload.Digest, payload.SizeBytes));

        Assert.Equal(1, fixture.Delivery.MaterializePending());
        var expanded = fixture.Delivery.ReadPendingForRun(run);
        Assert.True(expanded.IsExpanded);
        Assert.Equal(taskCount, expanded.Queued);
        Assert.Equal(taskCount, fixture.Count("notification_deliveries"));
        DeliveryReferenceIndex.VerifyReadOnly(instance, database, new());
        Assert.True(DeliveryReferenceIndex.IsSecretReferenced(instance, database, new(), payload.SecretId, payload.Digest, payload.SizeBytes));

        var lastTarget = $" WHERE task_id='{fixture.TaskIds[^1]:D}';";
        fixture.Sql("UPDATE notifications.notification_deliveries SET size_bytes=size_bytes+1" + lastTarget);
        try { Assert.ThrowsAny<IOException>(() => fixture.Delivery.ReadPendingForRun(run)); }
        finally { fixture.Sql("UPDATE notifications.notification_deliveries SET size_bytes=size_bytes-1" + lastTarget); }
        Assert.Equal(expanded, fixture.Delivery.ReadPendingForRun(run));

        if (taskCount > 1)
        {
            var sourceId = fixture.Snapshot.Document.OperationId;
            BlobReference original;
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true,
                domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications))
            using (var transaction = DatabaseConnections.BeginRead(connection))
            {
                original = Assert.IsType<BlobReference>(ReceiptRouteRows.Find(connection, transaction, sourceId, 0));
                transaction.Commit();
            }
            var route = new NotificationCatalogCodec(instance, new()).ReadDocument<ReceiptRouteDocument>(original);
            var first = route.Targets[0];
            var second = route.Targets[1];
            Assert.NotNull(first.Delivery);
            Assert.NotNull(second.Delivery);
            Assert.NotEqual(first.Delivery, second.Delivery);
            var swapped = route with { Targets = route.Targets.SetItem(0, first with { Delivery = second.Delivery })
                .SetItem(1, second with { Delivery = first.Delivery }) };
            var replacement = new ImmutableBlobStore(instance, new()).PersistPlan(NotificationCatalogCodec.Encode(swapped));
            StoreRoute(replacement);
            try { Assert.ThrowsAny<IOException>(() => fixture.Delivery.ReadPendingForRun(run)); }
            finally { StoreRoute(original); }
            Assert.Equal(expanded, fixture.Delivery.ReadPendingForRun(run));

            void StoreRoute(BlobReference reference) => fixture.Sql(FormattableString.Invariant(
                $"UPDATE notifications.notification_receipt_routes SET relative_path='{reference.RelativePath}',size_bytes={reference.SizeBytes},sha256='{reference.Sha256}' WHERE operation_id='{sourceId:D}' AND ordinal=0;"));
        }
    }

    [Fact(DisplayName = "Damaged Source Metadata Is Rejected Before Its Old Credential Can Be Retired")]
    public async Task RejectsDamagedSources()
    {
        using var fixture = new NotificationReceiptFixture(); await fixture.Execute();
        var before = fixture.Count("operations");
        fixture.Sql("UPDATE notifications.notification_receipt_sources SET route_count=route_count+1;");
        var payload = Assert.Single(Assert.Single(fixture.Snapshot.Document.Tasks).Secrets.Where(item => item.Payload is not null)).Payload!;
        Assert.ThrowsAny<Exception>(() => DeliveryReferenceIndex.IsSecretReferenced(fixture.Source.Instance,
            fixture.Source.Database, new(), payload.SecretId, payload.Digest, payload.SizeBytes));
        Assert.ThrowsAny<Exception>(() => fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId));
        Assert.Equal(before, fixture.Count("operations"));
    }

    private static ReceiptReadContext ReadReceipt(NotificationReceiptFixture fixture)
    {
        var source = fixture.Source;
        using var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.All);
        using var transaction = DatabaseConnections.BeginRead(connection);
        var result = new NotificationReceiptCodec(source.Instance, source.Database, new())
            .ReadContext(fixture.Snapshot.Reference, connection, transaction);
        transaction.Commit(); return result;
    }
}
