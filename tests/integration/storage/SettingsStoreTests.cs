using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class SettingsStoreTests
{
    [Fact(DisplayName = "Saved Ordinary Settings And Disabled Uninstalled Extension Preferences Survive An Independent Reopen")]
    public void ReopensSavedPreferences()
    {
        var fixture = Create();
        AssertSame(new(0, HostSettings.Default), fixture.Store.Read());
        var values = HostSettings.Default with { LogLevel = HostLogLevel.Fatal, Locale = HostLocale.EnUs,
            Proxy = new(ProxyMode.Http, "https://proxy.invalid:8443", "fixture-user"),
            Extensions = [new("uninstalled-extension", false), new("another-extension", true)] };
        var change = new HostSettingsChange(0, values);
        Assert.True(Save(fixture.Store, change).IsNew);
        var reopened = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "diagnostics"), new JournalStartupAdmission(fixture.Instance, new()));
        AssertSame(HostSettingsPolicy.Next(new(0, HostSettings.Default), 0, values), new HostSettingsStore(reopened).Read());
    }

    [Fact(DisplayName = "Automatic Ports And Settings Revisions Persist In The Current Catalog")]
    public void PersistsAutomaticPort()
    {
        var fixture = Create();
        var values = HostSettings.Default with { Service = HostSettings.Default.Service with { WebPort = 61000 },
            Extensions = [new("retained-extension", false)] };
        var first = new HostSettingsChange(0, values);
        var identity = HostSettingsStore.Identity(Guid.NewGuid(), "fixture", first);
        var accepted = fixture.Store.Commit(identity, first, DateTimeOffset.UtcNow);
        Save(fixture.Store, new(1, values with { HistoryRetentionDays = 30 }));
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var reopened = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "automatic-port"),
            new JournalStartupAdmission(fixture.Instance, new()), lease);
        var store = new HostSettingsStore(reopened);
        AssertSame(accepted.Saved, store.Commit(identity, first, DateTimeOffset.UtcNow).Saved);
        Assert.Equal(2, store.Read().Revision);
        Assert.Equal(30, store.Read().Values.HistoryRetentionDays);
        Assert.Equal(new[] { new ExtensionPreference("retained-extension", false) }, store.Read().Values.Extensions);
        var automatic = store.Read().Values with { Service = values.Service with { WebPort = 0, McpEnabled = true } };
        Save(store, new(2, automatic));
        lease.Dispose();
        var current = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "automatic-reopen"),
            new JournalStartupAdmission(fixture.Instance, new()));
        AssertSame(new(3, automatic), new HostSettingsStore(current).Read());
    }

    [Fact(DisplayName = "Repeated Settings Operations Return The Original Snapshot Without Restoring It Over Newer Choices")]
    public void ReplaysOriginalSnapshot()
    {
        var fixture = Create();
        var change = new HostSettingsChange(0, HostSettings.Default with { HistoryRetentionDays = 30 });
        var operation = HostSettingsStore.Identity(Guid.NewGuid(), "fixture", change);
        var first = fixture.Store.Commit(operation, change, DateTimeOffset.UtcNow);
        Save(fixture.Store, new(1, change.Values with { HistoryRetentionDays = 60 }));
        var replay = fixture.Store.Commit(operation, change, DateTimeOffset.UtcNow);
        Assert.False(replay.IsNew);
        AssertSame(first.Saved, replay.Saved);
        Assert.Equal(60, fixture.Store.Read().Values.HistoryRetentionDays);
        Assert.Equal(2, fixture.Store.Read().Revision);
        Assert.Throws<OperationConflictException>(() => fixture.Store.Commit(
            HostSettingsStore.Identity(operation.OperationId, "another-subject", change), change, DateTimeOffset.UtcNow));
        var changedRequest = change with { Values = change.Values with { HistoryRetentionDays = 40 } };
        Assert.Throws<OperationConflictException>(() => fixture.Store.Commit(
            HostSettingsStore.Identity(operation.OperationId, "fixture", changedRequest), changedRequest, DateTimeOffset.UtcNow));
    }

    [Fact(DisplayName = "Concurrent Different Settings Edits Have One Winner And The Losing Identity Can Be Corrected")]
    public async Task SerializesSettingsEdits()
    {
        var fixture = Create();
        var changes = new[] { new HostSettingsChange(0, HostSettings.Default with { HistoryRetentionDays = 10 }),
            new HostSettingsChange(0, HostSettings.Default with { HistoryRetentionDays = 20 }) };
        var identities = changes.Select(value => HostSettingsStore.Identity(Guid.NewGuid(), "fixture", value)).ToArray();
        var results = await Task.WhenAll(changes.Select((change, index) => Task.Run(() =>
            Record.Exception(() => fixture.Store.Commit(identities[index], change, DateTimeOffset.UtcNow)))));
        Assert.Single(results, result => result is null);
        var loser = Array.FindIndex(results, result => result is HostSettingsException);
        Assert.InRange(loser, 0, 1);
        Assert.Equal("settings_revision_conflict", results[loser]!.Message);
        var corrected = changes[loser] with { ExpectedRevision = 1 };
        var result = fixture.Store.Commit(HostSettingsStore.Identity(identities[loser].OperationId, "fixture", corrected), corrected, DateTimeOffset.UtcNow);
        Assert.Equal(2, result.Saved.Revision);
    }

    [Theory(DisplayName = "A Rejected Settings Save Cannot Consume Its Identity Or Publish Any Of Its Fields")]
    [InlineData("invalid")]
    [InlineData("startup-effect")]
    [InlineData("sql-failure")]
    public void RejectsInvalidSave(string fault)
    {
        var fixture = Create();
        var id = Guid.NewGuid();
        var values = HostSettings.Default with { HistoryRetentionDays = fault == "invalid" ? 0 : 20, AutoStart = fault == "startup-effect" };
        var change = new HostSettingsChange(0, values);
        if (fault == "sql-failure") Sql(fixture.Database,
            "CREATE TRIGGER system.reject_settings BEFORE INSERT ON settings_mutations BEGIN SELECT RAISE(ABORT,'fixture-failure'); END;");
        Assert.NotNull(Record.Exception(() => fixture.Store.Commit(HostSettingsStore.Identity(id, "fixture", change), change, DateTimeOffset.UtcNow)));
        AssertSame(new(0, HostSettings.Default), fixture.Store.Read());
        if (fault == "sql-failure") Sql(fixture.Database, "DROP TRIGGER system.reject_settings;");
        var corrected = new HostSettingsChange(0, HostSettings.Default with { HistoryRetentionDays = 40 });
        Assert.True(fixture.Store.Commit(HostSettingsStore.Identity(id, "fixture", corrected), corrected, DateTimeOffset.UtcNow).IsNew);
    }

    [Theory(DisplayName = "Unknown Persisted Settings Are Rejected On The Diagnostic Copy Without Repairing The Original")]
    [InlineData("missing-current")]
    [InlineData("invalid-url")]
    public void PreservesUnknownSettings(string fault)
    {
        var fixture = Create();
        Sql(fixture.Database, fault == "missing-current" ? "DELETE FROM system.settings_current;" : "UPDATE system.settings_snapshots SET proxy_url='file:///private';");
        var before = Fingerprints(fixture.Database);
        Assert.NotNull(Record.Exception(() => StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "diagnostics"),
            new JournalStartupAdmission(fixture.Instance, new()))));
        Assert.Equal(before, Fingerprints(fixture.Database));
    }

    private static SettingsSaveResult Save(HostSettingsStore store, HostSettingsChange change) =>
        store.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);

    private static void AssertSame(HostSettingsSnapshot expected, HostSettingsSnapshot actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    private static Fixture Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "settings-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        return new(root, instance, database, new(database));
    }

    private static void Sql(StateDatabase database, string sql)
    {
        using var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open($"Data Source={database.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string[] Fingerprints(StateDatabase database) => new[] { "", "-wal", "-shm", "-journal" }
        .Where(suffix => File.Exists(database.DatabasePath + suffix))
        .Select(suffix => suffix + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(database.DatabasePath + suffix)))).ToArray();

    private sealed record Fixture(string Root, RuntimeInstance Instance, StateDatabase Database, HostSettingsStore Store);
}
