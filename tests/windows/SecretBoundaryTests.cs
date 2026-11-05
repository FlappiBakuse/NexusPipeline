using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Secrets;
using Xunit;

namespace NexusPipeline.WindowsTests;

[SupportedOSPlatform("windows")]
public sealed class SecretBoundaryTests
{
    private const string Canary = "notification-boundary-existing-dpapi-canary";

    [Theory(DisplayName = "Generic Secret Mutation And Standalone Preparation Cannot Publish Notification Credentials")]
    [InlineData(SecretDisposition.Keep)]
    [InlineData(SecretDisposition.Set)]
    [InlineData(SecretDisposition.Clear)]
    public async Task RequiresNotificationOwner(SecretDisposition disposition)
    {
        using var fixture = SecretTestRuntime.Create();
        using var material = new SecretValue(Canary);
        var existing = await fixture.Service.ApplyAsync(Guid.NewGuid(), fixture.Key, 0, SecretDisposition.Set, material, default);
        var key = SecretFieldKey.NotificationTask(fixture.Instance.InstanceId, Guid.NewGuid(), NotificationSecretField.WebhookUrl);
        var operation = Guid.NewGuid(); var before = Snapshot(fixture);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await fixture.Service.ApplyAsync(operation, key, 0, disposition, disposition == SecretDisposition.Set ? material : null, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await fixture.Store.ApplyAsync(operation, key, 0, disposition, disposition == SecretDisposition.Set ? material : null, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => { using var ignored = await fixture.Service.ReadAsync(key, default); });
        Assert.Throws<UnauthorizedAccessException>(() =>
            new PreparationPlanCodec(fixture.Instance, new(), fixture.Database).Freeze(
                NexusPipeline.Platform.Storage.Sqlite.SecretFieldStore.Identity(operation, key, 0, SecretDisposition.Set),
                Guid.NewGuid(), key, 0, material));
        var after = Snapshot(fixture);
        Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        Assert.Equal(existing, await fixture.Service.GetStateAsync(fixture.Key, default));
        using var original = await fixture.Service.ReadAsync(fixture.Key, default);
        original.Use(value => Assert.True(value.SequenceEqual(Canary)));
        Assert.False((await fixture.Service.GetStateAsync(key, default)).IsConfigured);
    }

    [Fact(DisplayName = "A Foreign Notification Instance Cannot Read Status Or Create Any Preparation")]
    public async Task RejectsForeignInstances()
    {
        using var fixture = SecretTestRuntime.Create();
        var key = SecretFieldKey.NotificationTask(Guid.NewGuid(), Guid.NewGuid(), NotificationSecretField.SmtpPassword);
        var before = Snapshot(fixture);
        var error = await Assert.ThrowsAsync<IOException>(async () => await fixture.Store.GetStateAsync(key, default));
        Assert.Equal("secret_notification_instance_unbound", error.Message);
        var after = Snapshot(fixture);
        Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }

    private static (string[] Rows, string[] Files) Snapshot(SecretTestRuntime fixture)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Database.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); using var transaction = connection.BeginTransaction(deferred: true);
        var rows = new List<string>();
        foreach (var table in new[] { "operations", "operation_journals", "secret_fields", "secret_payloads",
            "secret_mutations", "secret_preparations", "secret_retirements", "resource_isolations" })
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT * FROM " + table + ";";
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(table + ":" + JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray()));
        }
        var files = Directory.EnumerateFiles(fixture.Instance.RuntimeRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "instance.lock" &&
                !StringComparer.OrdinalIgnoreCase.Equals(path, fixture.Database.DatabasePath) &&
                !StringComparer.OrdinalIgnoreCase.Equals(path, fixture.Database.DatabasePath + "-wal") &&
                !StringComparer.OrdinalIgnoreCase.Equals(path, fixture.Database.DatabasePath + "-shm"))
            .Select(path => Path.GetRelativePath(fixture.Instance.RuntimeRoot, path) + ":" +
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))) .Order(StringComparer.Ordinal).ToArray();
        return (rows.Order(StringComparer.Ordinal).ToArray(), files);
    }
}
