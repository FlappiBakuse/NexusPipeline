using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class CatalogPublicationTests
{
    [Theory(DisplayName = "The Final Catalog And All Five Credential References Have One Sql Commit Or No Result")]
    [InlineData("last-field")]
    [InlineData("final-outcome")]
    public void CommitsCatalogAtomically(string fault)
    {
        using var runtime = SecretTestRuntime.Create();
        var publication = PrepareClear(runtime);
        var last = publication.Document.Secrets[^1].OperationId.ToString("D");
        var sql = fault == "last-field"
            ? $"CREATE TRIGGER notification_fault BEFORE INSERT ON secret_mutations WHEN NEW.operation_id='{last}' BEGIN SELECT RAISE(ABORT,'notification_fault'); END;"
            : "CREATE TRIGGER notification_fault BEFORE INSERT ON notification_publication_outcomes BEGIN SELECT RAISE(ABORT,'notification_fault'); END;";
        using (var connection = DatabaseConnections.Open(runtime.Database.DatabasePath))
        using (var command = connection.CreateCommand()) { command.CommandText = sql; command.ExecuteNonQuery(); }
        Assert.Throws<SqliteException>(() => Commit(runtime, publication));
        using (var connection = DatabaseConnections.Open(runtime.Database.DatabasePath, readOnly: true))
        {
            Assert.Null(NotificationCatalogRows.ReadCurrent(connection, null));
            foreach (var table in new[] { "notification_publications", "notification_publication_fields",
                         "notification_publication_outcomes", "notification_task_ids", "secret_fields", "secret_mutations", "operations" })
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM " + table + ";";
                Assert.Equal(0L, (long)command.ExecuteScalar()!);
            }
        }
        using (var connection = DatabaseConnections.Open(runtime.Database.DatabasePath))
        using (var command = connection.CreateCommand()) { command.CommandText = "DROP TRIGGER notification_fault;"; command.ExecuteNonQuery(); }
        Commit(runtime, publication);
        using (var connection = DatabaseConnections.Open(runtime.Database.DatabasePath, readOnly: true))
        {
            var replay = NotificationCatalogRows.Lookup(connection, null, NotificationPublicationCodec.Identity(publication.Document))
                ?? throw new IOException("notification_replay_missing");
            Assert.Equal("completed", replay.Status);
            Assert.Equal(publication.After.Reference, NotificationCatalogRows.ReadCurrent(connection, null)!.Reference);
            foreach (var item in publication.Document.Secrets)
            {
                var key = SecretFieldKey.NotificationTask(runtime.Instance.InstanceId, publication.Document.Request.TaskId!.Value, item.Field);
                Assert.Equal(new SecretFieldReference(1, null), SecretFieldRows.ReadField(connection, null, key));
                NotificationCatalogRows.RequireFieldProvenance(connection, null, runtime.Instance.InstanceId, key.SubjectId, key.Field, item.After);
            }
        }
    }

    [Fact(DisplayName = "Ninety Nine Maximum Templates Use Separate Immutable Task Definitions Within The Metadata Limit")]
    public void BoundsMaximumTemplates()
    {
        using var runtime = SecretTestRuntime.Create();
        var configuration = NotificationChannelConfiguration.Default with { WebhookTemplate = new string('x', 65536) };
        var catalog = new NotificationTaskCatalog(99, 99, Enumerable.Range(0, 99).Select(index =>
            new NotificationTaskDefinition(Guid.NewGuid(), 1, $"task-{index}", "", NotificationTaskKind.Webhook, true, configuration))
            .ToImmutableArray());
        var snapshot = new NotificationCatalogCodec(runtime.Instance, new()).Persist(catalog);
        Assert.True(snapshot.Reference.SizeBytes < StrictJson.MaximumBytes);
        Assert.All(snapshot.Document.Tasks, entry => Assert.True(entry.Definition.SizeBytes < StrictJson.MaximumBytes));
        Assert.True(snapshot.Document.Tasks.Sum(entry => entry.Definition.SizeBytes) > StrictJson.MaximumBytes);
        Assert.Equal(catalog.Revision, snapshot.Catalog.Revision);
        Assert.Equal(catalog.MaximumTasks, snapshot.Catalog.MaximumTasks);
        Assert.Equal(catalog.Tasks.ToArray(), snapshot.Catalog.Tasks.ToArray());
        Assert.Equal(99, snapshot.Catalog.Tasks.Length);
        Assert.All(snapshot.Catalog.Tasks, task => Assert.Equal(configuration, task.Configuration));
    }

    [Theory(DisplayName = "Saved Catalog Metadata Does Not Accept Unknown Or Missing Members")]
    [InlineData("unknown")]
    [InlineData("missing")]
    public void RejectsUnknownMetadata(string damage)
    {
        using var runtime = SecretTestRuntime.Create();
        var codec = new NotificationCatalogCodec(runtime.Instance, new());
        var initial = codec.Persist(NotificationTaskCatalog.Empty);
        using var input = new ImmutableBlobStore(runtime.Instance, new()).OpenVerified(initial.Reference);
        using var reader = new StreamReader(input, Encoding.UTF8);
        var text = reader.ReadToEnd();
        text = damage == "unknown" ? text.Replace("{", "{\"unknown\":0,", StringComparison.Ordinal)
            : text.Replace("\"maximumTasks\":10,", "", StringComparison.Ordinal);
        var reference = new ImmutableBlobStore(runtime.Instance, new()).PersistPlan(Encoding.UTF8.GetBytes(text));
        Assert.Throws<JsonException>(() => codec.Read(reference));
    }

    private static NotificationPublicationSnapshot PrepareClear(SecretTestRuntime runtime)
    {
        var codec = new NotificationCatalogCodec(runtime.Instance, new());
        var before = codec.Persist(NotificationTaskCatalog.Empty);
        var operationId = Guid.NewGuid();
        var request = new NotificationCatalogRequest(NotificationCatalogChange.Save, 0, Guid.NewGuid(), 0,
            new("task", "", NotificationTaskKind.Webhook, true, NotificationChannelConfiguration.Default), [], null);
        var after = codec.Persist(NotificationPublicationCodec.Apply(before.Catalog, request));
        var fields = Enum.GetValues<NotificationSecretField>().Select(field => new NotificationSecretChange(field,
            NotificationPublicationCodec.ChildOperation(operationId, field), SecretDisposition.Clear,
            new(0, null), new(1, null), false, null, null, null)).ToImmutableArray();
        var document = new NotificationPublicationDocument("nexus.notification.publication", 1, runtime.Instance.InstanceId,
            operationId, "local:fixture", NotificationPublicationCodec.Digest(runtime.Instance.InstanceId, "local:fixture", request, fields),
            request, before.Reference, after.Reference, fields);
        return new NotificationPublicationCodec(runtime.Instance, new()).Persist(document);
    }

    private static void Commit(SecretTestRuntime runtime, NotificationPublicationSnapshot publication)
    {
        runtime.Database.Write((connection, transaction) =>
        {
            Assert.True(NotificationCatalogRows.AcceptInside(connection, transaction, publication, DateTimeOffset.UtcNow));
            foreach (var field in publication.Document.Secrets)
            {
                var key = SecretFieldKey.NotificationTask(runtime.Instance.InstanceId, publication.Document.Request.TaskId!.Value, field.Field);
                var operation = SecretFieldStore.Identity(field.OperationId, key, field.Before.Revision, field.Disposition);
                _ = OperationRows.Accept(connection, transaction, operation, DateTimeOffset.UtcNow);
                SecretFieldStore.WriteResult(connection, transaction, field.OperationId, key, field.After, null, false, null);
            }
            NotificationCatalogRows.CommitCatalogInside(connection, transaction, publication);
            return true;
        });
    }
}
