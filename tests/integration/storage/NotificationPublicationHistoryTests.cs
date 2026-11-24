using System.Runtime.Versioning;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class NotificationPublicationHistoryTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "Publication History Opens A Protected Catalog Once Per Shared Revision And Rechecks It On The Next Audit")]
    public async Task SharesProtectedCatalogRevisions()
    {
        using var fixture = new ExecutionAdmissionFixture();
        var source = fixture.Source;
        var instance = source.Instance;
        var catalog = new NotificationCatalogService(instance, source.Database, source.Lease, fixture.Resources, fixture.Index, new());
        const string subject = "fixture:publication-history";
        _ = await catalog.SaveAsync(Guid.NewGuid(), subject, 0, Guid.NewGuid(), 0,
            new("historical task", "", NotificationTaskKind.Webhook, false, NotificationChannelConfiguration.Default), []);
        var shared = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var revision = 1; revision < 12; revision++)
        {
            using (var reader = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true,
                domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications))
            {
                var cursor = NotificationCatalogRows.ReadCurrent(reader, null)!;
                shared.Add(Path.GetFullPath(Path.Combine(instance.RuntimeRoot, cursor.Reference.RelativePath)), 0);
            }
            var result = await catalog.SetMaximumAsync(Guid.NewGuid(), subject, revision, revision % 2 == 1 ? 11 : 12);
            Assert.Equal(revision + 1, result.Revision);
        }

        var inventory = new NotificationPublicationInventory(instance, source.Database, new());
        using (BlobReadScope.Capture(instance, path =>
        {
            if (shared.TryGetValue(path, out var opens)) shared[path] = opens + 1;
        })) inventory.VerifyReadOnly();

        var old = shared.Keys.First();
        var bytes = File.ReadAllBytes(old);
        bytes[0] ^= 1;
        File.WriteAllBytes(old, bytes);
        Assert.ThrowsAny<IOException>(() => inventory.VerifyReadOnly());
        output.WriteLine($"catalog audit: {shared.Count} shared revisions, {shared.Values.Sum()} verified-open calls");
        Assert.All(shared.Values, opens => Assert.Equal(1, opens));
    }

    [Fact(DisplayName = "Catalog History Uses One Read Snapshot And Still Rejects A Missing Historical Outcome")]
    public async Task AuditsPublishedHistoryInOneSnapshot()
    {
        using var fixture = new ExecutionAdmissionFixture();
        var source = fixture.Source;
        source.Lease.BindDatabase(source.Database);
        var catalog = new NotificationCatalogService(source.Instance, source.Database, source.Lease,
            fixture.Resources, fixture.Index, new());
        var first = Guid.NewGuid();
        const string subject = "fixture:publication-history";
        for (var index = 0; index < 12; index++)
        {
            var result = await catalog.SetMaximumAsync(index == 0 ? first : Guid.NewGuid(), subject, index,
                index % 2 == 0 ? 11 : 12);
            Assert.Equal(index + 1, result.Revision);
        }
        Assert.Equal(1, (await catalog.SetMaximumAsync(first, subject, 0, 11)).Revision);
        Assert.Equal(12, catalog.Read().Revision);

        var path = source.Database.DatabasePath;
        var transactions = 0;
        SQLitePCL.sqlite3 handle;
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
        {
            if (sql.StartsWith("BEGIN", StringComparison.Ordinal)) transactions++;
        };
        using (var reader = DatabaseConnections.Open(path, readOnly: true,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System))
        {
            handle = reader.Handle!;
            SQLitePCL.raw.sqlite3_trace(handle, trace, null);
        }
        var inventory = new NotificationPublicationInventory(source.Instance, source.Database, new());
        try { inventory.VerifyReadOnly(); }
        finally { SQLitePCL.raw.sqlite3_trace(handle, (SQLitePCL.strdelegate_trace?)null, null); }

        using (var writer = DatabaseConnections.Open(path, allowPooling: false,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications))
        using (var remove = DatabaseConnections.Command(writer, null,
            "DELETE FROM notifications.notification_publication_outcomes WHERE operation_id=$0;", first.ToString("D")))
            Assert.Equal(1, remove.ExecuteNonQuery());
        var rejected = await Assert.ThrowsAsync<IOException>(() => catalog.SetMaximumAsync(first, subject, 0, 11).AsTask());
        Assert.Equal("notification_publication_outcome_unbound", rejected.Message);
        Assert.Equal(12, catalog.Read().Revision);
        output.WriteLine($"catalog audit: 12 publications, {transactions} SQLite read transactions");
        Assert.Equal(1, transactions);
    }

    [Fact(DisplayName = "A Replayed Catalog Request Reuses Its Validated History Until An Independent Commit Changes It")]
    public async Task ReusesUnchangedReplayHistory()
    {
        using var fixture = new ExecutionAdmissionFixture();
        var source = fixture.Source;
        source.Lease.BindDatabase(source.Database);
        var catalog = new NotificationCatalogService(source.Instance, source.Database, source.Lease,
            fixture.Resources, fixture.Index, new());
        var first = Guid.NewGuid();
        const string subject = "fixture:catalog-replay";
        for (var revision = 0; revision < 12; revision++)
            _ = await catalog.SetMaximumAsync(revision == 0 ? first : Guid.NewGuid(), subject, revision, revision % 2 == 0 ? 11 : 12);
        Assert.Equal(1, (await catalog.SetMaximumAsync(first, subject, 0, 11)).Revision);

        var path = source.Database.DatabasePath;
        SQLitePCL.sqlite3 handle;
        var enumerations = 0;
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
        {
            if (sql.Contains("FROM notifications.notification_publications ORDER BY after_revision,operation_id", StringComparison.Ordinal))
                enumerations++;
        };
        using (var reader = DatabaseConnections.Open(path, readOnly: true,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System))
        {
            handle = reader.Handle!;
            SQLitePCL.raw.sqlite3_trace(handle, trace, null);
        }
        try { Assert.Equal(1, (await catalog.SetMaximumAsync(first, subject, 0, 11)).Revision); }
        finally { SQLitePCL.raw.sqlite3_trace(handle, (SQLitePCL.strdelegate_trace?)null, null); }

        using (var writer = DatabaseConnections.Open(path, allowPooling: false,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications))
        using (var remove = DatabaseConnections.Command(writer, null,
            "DELETE FROM notifications.notification_publication_outcomes WHERE operation_id=$0;", first.ToString("D")))
            Assert.Equal(1, remove.ExecuteNonQuery());
        var error = await Assert.ThrowsAsync<IOException>(() => catalog.SetMaximumAsync(first, subject, 0, 11).AsTask());
        Assert.Equal("notification_publication_outcome_unbound", error.Message);
        Assert.Equal(12, catalog.Read().Revision);
        output.WriteLine($"catalog replay: 12 publications, {enumerations} history enumerations");
        Assert.Equal(0, enumerations);
    }

    [Theory(DisplayName = "Consecutive Catalog Changes Extend Protected History And Detect An Independent Historical Mutation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtendsVerifiedLimitHistory(bool reorder)
    {
        using var fixture = new ExecutionAdmissionFixture();
        var source = fixture.Source;
        source.Lease.BindDatabase(source.Database);
        var catalog = new NotificationCatalogService(source.Instance, source.Database, source.Lease,
            fixture.Resources, fixture.Index, new());
        var first = Guid.NewGuid();
        const string subject = "fixture:catalog-limit-history";
        var id = Guid.NewGuid(); var next = Guid.NewGuid();
        var start = 0;
        if (reorder)
        {
            var task = new NotificationTaskInput("fixture", "", NotificationTaskKind.Webhook, false, NotificationChannelConfiguration.Default);
            _ = await catalog.SaveAsync(Guid.NewGuid(), subject, 0, id, 0, task, []);
            _ = await catalog.SaveAsync(Guid.NewGuid(), subject, 1, next, 0, task with { Name = "fixture second" }, []);
            start = 2;
        }
        for (var revision = start; revision < 12; revision++)
            _ = await catalog.SetMaximumAsync(revision == start ? first : Guid.NewGuid(), subject, revision, revision % 2 == 0 ? 11 : 12);
        Assert.Equal(start + 1, (await catalog.SetMaximumAsync(first, subject, start, 11)).Revision);

        var path = source.Database.DatabasePath;
        var enumerations = 0;
        var appends = 0;
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
        {
            if (sql.Contains("FROM notifications.notification_publications ORDER BY after_revision,operation_id", StringComparison.Ordinal))
                enumerations++;
            if (sql.Contains("WHERE operation_id=", StringComparison.Ordinal) && sql.Contains("AND before_revision=", StringComparison.Ordinal)) appends++;
        };
        var readerHandle = Instrument(readOnly: true,
            DatabaseDomain.Runtime | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System);
        var writerHandle = Instrument(readOnly: false, DatabaseDomain.All);
        try
        {
            var changed = reorder ? await catalog.ReorderAsync(Guid.NewGuid(), subject, 12, [next, id]) :
                await catalog.SetMaximumAsync(Guid.NewGuid(), subject, 12, 13);
            Assert.Equal(13, changed.Revision);
            if (reorder) Assert.Equal([next, id], changed.Tasks.Select(task => task.Id));
            Assert.Equal(14, (await catalog.SetMaximumAsync(Guid.NewGuid(), subject, 13, 14)).Revision);
        }
        finally
        {
            foreach (var handle in new[] { readerHandle, writerHandle })
                SQLitePCL.raw.sqlite3_trace(handle, (SQLitePCL.strdelegate_trace?)null, null);
        }

        using (var writer = DatabaseConnections.Open(path, allowPooling: false,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications))
        using (var remove = DatabaseConnections.Command(writer, null,
            "DELETE FROM notifications.notification_publication_outcomes WHERE operation_id=$0;", first.ToString("D")))
            Assert.Equal(1, remove.ExecuteNonQuery());
        var error = await Assert.ThrowsAsync<IOException>(() => catalog.SetMaximumAsync(Guid.NewGuid(), subject, 14, 15).AsTask());
        Assert.Equal("notification_publication_outcome_unbound", error.Message);
        Assert.Equal(14, catalog.Read().Revision);
        output.WriteLine($"catalog changes: 12 historical publications plus 2 actual changes, {enumerations} history enumerations, {appends} verified append rows");
        Assert.Equal(2, appends);
        Assert.Equal(0, enumerations);

        SQLitePCL.sqlite3 Instrument(bool readOnly, DatabaseDomain domains)
        {
            using var connection = DatabaseConnections.Open(path, readOnly: readOnly, domains: domains);
            var handle = connection.Handle!;
            SQLitePCL.raw.sqlite3_trace(handle, trace, null);
            return handle;
        }
    }

    [Theory(DisplayName = "An Unregistered Notification Work Source Still Stops A Catalog Change")]
    [InlineData("notification.manual-source", "host:manual-provider")]
    [InlineData("notification.scheduled-source", "host:scheduled-provider")]
    public async Task RejectsUnregisteredWorkSource(string kind, string subject)
    {
        using var fixture = new ExecutionAdmissionFixture();
        var source = fixture.Source;
        using (var connection = DatabaseConnections.Open(source.Database.DatabasePath))
        using (var insert = DatabaseConnections.Command(connection, null, """
            INSERT INTO main.operations(operation_id,subject_id,request_digest,operation_kind,status,created_at)
            VALUES($0,$1,$2,$3,'completed','2026-01-01T00:00:00Z');
            """, Guid.NewGuid().ToString("D"), subject, new string('a', 64), kind))
            Assert.Equal(1, insert.ExecuteNonQuery());

        var catalog = new NotificationCatalogService(source.Instance, source.Database, source.Lease,
            fixture.Resources, fixture.Index, new());
        var error = await Assert.ThrowsAsync<IOException>(() => catalog.SetMaximumAsync(Guid.NewGuid(), subject, 0, 11).AsTask());
        Assert.Equal("notification_publication_inventory_unknown", error.Message);
        Assert.Equal(0, catalog.Read().Revision);
    }
}
