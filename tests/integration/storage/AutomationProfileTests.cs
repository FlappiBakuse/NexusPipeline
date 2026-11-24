using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class AutomationProfileTests
{
    [Fact]
    public void RoundTripsProfiles()
    {
        var fixture = Create(); var seed = AutomationDefinitionSeed.Create(fixture.Root);
        var first = seed.Scripts[0] with { Order = 2 };
        var second = first with { Id = Guid.NewGuid(), Name = "Second", Order = 0 };
        var original = seed.Schedules[0] with { Order = 2 };
        var other = original with { Id = Guid.NewGuid(), Order = 0, LocalMinute = 750 };
        var changes = seed with { Scripts = [first, second], Schedules = [original, other],
            Users = [seed.Users[0] with { Notes = "Fixture notes\n第二行", Avatar = Avatar }, seed.Users[1]],
            Queues = [seed.Queues[0] with { ScheduleEnabled = false }] };
        fixture.Store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        var reopened = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "reopened"));
        var catalog = new AutomationDefinitionStore(reopened).Read();
        Assert.Equal(new[] { second.Id, first.Id }, catalog.Scripts.Select(item => item.Id));
        Assert.Equal(new[] { other.Id, original.Id }, catalog.Schedules.Select(item => item.Id));
        Assert.Equal(changes.Users[0].Notes, catalog.Users[0].Notes);
        Assert.Equal(Avatar, catalog.Users[0].Avatar);
        Assert.False(catalog.Queues[0].ScheduleEnabled);
        var contracts = new ContractReader();
        var codec = new AutomationBundleCodec(contracts);
        var export = codec.Encode(catalog, DateTimeOffset.UtcNow);
        using var exported = contracts.Read(export, "automation-bundle.schema.json");
        var wire = exported.RootElement;
        Assert.Equal("nexus.automation.bundle", wire.GetProperty("format").GetString());
        Assert.Equal(1, wire.GetProperty("version").GetInt32());
        Assert.Equal("definitions", wire.GetProperty("exportKind").GetString());
        var profile = Assert.Single(wire.GetProperty("users").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == seed.Users[0].Id);
        Assert.Equal("Fixture notes\n第二行", profile.GetProperty("notes").GetString());
        Assert.Equal(Avatar, profile.GetProperty("avatar").GetString());
        static (Guid Id, int Order) Identity(JsonElement item) =>
            (item.GetProperty("id").GetGuid(), item.GetProperty("order").GetInt32());
        Assert.Equal(new[] { (second.Id, 0), (first.Id, 2) }, wire.GetProperty("scripts").EnumerateArray().Select(Identity));
        Assert.Equal(new[] { (other.Id, 0), (original.Id, 2) }, wire.GetProperty("schedules").EnumerateArray().Select(Identity));
        Assert.False(Assert.Single(wire.GetProperty("queues").EnumerateArray()).GetProperty("scheduleEnabled").GetBoolean());

        var incoming = """
            {"format":"nexus.automation.bundle","version":1,
             "bundleId":"137d9a02-b09d-4c94-896e-13f29b7b1da4","createdAt":"2026-05-04T08:30:00.0000000Z","exportKind":"definitions",
             "roots":[{"id":"abf8284f-7735-4e9c-9a63-3295230d817c","name":"Imported root","kind":"external"}],
             "scripts":[
               {"id":"db412785-4b36-425c-96c1-f9de02960965","name":"Ordered script","kind":"generic","order":4,
                "rootId":"abf8284f-7735-4e9c-9a63-3295230d817c",
                "executable":{"rootId":"abf8284f-7735-4e9c-9a63-3295230d817c","relative":"fixture.exe"},
                "arguments":[],"configurationPaths":[],"logPaths":[],"adapterResources":[],"updateConfigurationAtFinish":false,
                "limits":{"maximumAttempts":1,"stallTimeoutSeconds":0,"totalTimeoutSeconds":60},
                "judgement":{"language":"keywords","successKeywords":["fixture"],"failureKeywords":[]}}
             ],
             "bindings":[],"attachments":[],
             "queues":[{"id":"9eefb517-e084-42bf-b025-4a34d1edec34","name":"Imported queue","order":3,
               "scriptIds":["db412785-4b36-425c-96c1-f9de02960965"],"scheduleEnabled":false,
               "completionAction":"none","notifyEnabled":false,"notificationTaskIds":[]}],
             "schedules":[{"id":"ef315bea-2006-4d52-a9e7-13ffbabfe440","queueId":"9eefb517-e084-42bf-b025-4a34d1edec34",
               "order":5,"enabled":true,"timeZoneId":"UTC","weekdays":[1,3],"localMinute":620,"missedPolicy":"skip"}],
             "users":[
               {"id":"4d7f4484-a4b6-4650-8895-c1da5dfc909a","name":"Imported second","order":3},
               {"id":"62da08a0-724d-4342-9c56-f6b895953ae0","name":"Imported first","order":1,
                "notes":"Independent note\n导入","avatar":"AVATAR_BYTES"}
             ]}
            """.Replace("AVATAR_BYTES", Avatar, StringComparison.Ordinal);
        var target = Create();
        var importedRoot = Guid.Parse("abf8284f-7735-4e9c-9a63-3295230d817c");
        var importedScript = Guid.Parse("db412785-4b36-425c-96c1-f9de02960965");
        var importedQueue = Guid.Parse("9eefb517-e084-42bf-b025-4a34d1edec34");
        var chosenRoot = Directory.CreateDirectory(Path.Combine(target.Root, "selected")).FullName;
        var parsed = codec.Decode(Encoding.UTF8.GetBytes(incoming), new Dictionary<Guid, string> { [importedRoot] = chosenRoot }, 0);
        var import = AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture-import", parsed.Definitions);
        Assert.True(target.Store.Commit(import, parsed.Definitions, DateTimeOffset.UtcNow).IsNew);
        var importedDatabase = StateDatabase.OpenExisting(target.Instance, Path.Combine(target.Root, "imported"));
        var imported = new AutomationDefinitionStore(importedDatabase).Read();
        Assert.Equal(new[]
        {
            (Guid.Parse("62da08a0-724d-4342-9c56-f6b895953ae0"), "Imported first", 1, "Independent note\n导入", Avatar),
            (Guid.Parse("4d7f4484-a4b6-4650-8895-c1da5dfc909a"), "Imported second", 3, "", "")
        }, imported.Users.Select(item => (item.Id, item.Name, item.Order, item.Notes, item.Avatar)));
        var root = Assert.Single(imported.Roots);
        Assert.Equal((importedRoot, chosenRoot), (root.Id, root.Path));
        var script = Assert.Single(imported.Scripts);
        Assert.Equal((importedScript, importedRoot, 4, "fixture.exe"),
            (script.Id, script.RootId, script.Order, script.Executable.RelativePath));
        var queue = Assert.Single(imported.Queues);
        Assert.Equal((importedQueue, 3, false), (queue.Id, queue.Order, queue.ScheduleEnabled));
        Assert.Equal(importedScript, Assert.Single(queue.ScriptIds));
        var schedule = Assert.Single(imported.Schedules);
        Assert.Equal((Guid.Parse("ef315bea-2006-4d52-a9e7-13ffbabfe440"), importedQueue, 5, 620, true),
            (schedule.Id, schedule.QueueId, schedule.Order, schedule.LocalMinute, schedule.IsEnabled));
        Assert.Throws<AutomationConflictException>(() => fixture.Store.Commit(
            AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PreservesCurrentDefinitions()
    {
        var fixture = Create(); var seed = AutomationDefinitionSeed.Create(fixture.Root);
        fixture.Store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", seed), seed, DateTimeOffset.UtcNow);
        var before = fixture.Store.Read();
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "owned"),
            new JournalStartupAdmission(fixture.Instance, new()), lease);
        var migrated = new AutomationDefinitionStore(database).Read();
        Assert.Equal(before.Revision, migrated.Revision);
        Assert.Equal(before.Bindings.ToArray(), migrated.Bindings.ToArray());
        Assert.Equal(before.Scripts.Select(item => item.Id), migrated.Scripts.Select(item => item.Id));
        Assert.Equal(before.Users.ToArray(), migrated.Users.ToArray());
        Assert.True(migrated.Queues[0].ScheduleEnabled);
        Assert.Equal(NexusPipeline.Platform.Serialization.BoundedJson.Serialize(before),
            NexusPipeline.Platform.Serialization.BoundedJson.Serialize(migrated));
    }

    [Theory]
    [InlineData("data:text/html;base64,AAAA")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("data:image/png;base64,!!!!")]
    public void RejectsInvalidAvatar(string avatar)
    {
        var fixture = Create(); var seed = AutomationDefinitionSeed.Create(fixture.Root);
        var changes = seed with { Users = [seed.Users[0] with { Avatar = avatar }, seed.Users[1]] };
        var id = Guid.NewGuid();
        Assert.Throws<AutomationDefinitionException>(() => fixture.Store.Commit(
            AutomationDefinitionStore.Identity(id, "fixture", changes), changes, DateTimeOffset.UtcNow));
        Assert.Equal(0, fixture.Store.Read().Revision);
        Assert.True(fixture.Store.Commit(AutomationDefinitionStore.Identity(id, "fixture", seed), seed, DateTimeOffset.UtcNow).IsNew);
    }

    private static Fixture Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "profile-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        return new(root, instance, database, new(database));
    }

    private const string Avatar = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNQSjvzHwAEVgJUNpk8fQAAAABJRU5ErkJggg==";
    private sealed record Fixture(string Root, RuntimeInstance Instance, StateDatabase Database, AutomationDefinitionStore Store);
}
