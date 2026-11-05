using System.Text.Json.Nodes;
using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DefinitionOrderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsUnknownZones(bool enabled)
    {
        var fixture = Create(); var initial = AutomationDefinitionSeed.Create(fixture.Root);
        var invalid = initial with { Schedules = [initial.Schedules[0] with { IsEnabled = enabled, TimeZoneId = "nexus/no-such-zone" }] };
        var operation = Guid.NewGuid();
        Assert.Equal("automation_time_zone_unavailable", Assert.Throws<AutomationDefinitionException>(() => fixture.Store.Commit(
            AutomationDefinitionStore.Identity(operation, "fixture", invalid), invalid, DateTimeOffset.UtcNow)).Message);
        Assert.Equal(0, fixture.Store.Read().Revision);
        Assert.True(fixture.Store.Commit(AutomationDefinitionStore.Identity(operation, "fixture", initial), initial, DateTimeOffset.UtcNow).IsNew);
    }

    [Fact]
    public void PersistsQueueOrder()
    {
        var fixture = Create(); var initial = AutomationDefinitionSeed.Create(fixture.Root);
        fixture.Store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", initial), initial, DateTimeOffset.UtcNow);
        var catalog = fixture.Store.Read(); var first = catalog.Queues.Single();
        var second = first with { Id = Guid.NewGuid(), Revision = 0, Name = "Second", Order = 0 };
        var add = new AutomationChanges(catalog.Revision, [], [], [], [], [first with { Order = 1 }, second], [], []);
        fixture.Store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", add), add, DateTimeOffset.UtcNow);
        Assert.Equal(new[] { second.Id, first.Id }, fixture.Store.Read().Queues.Select(queue => queue.Id));
        catalog = fixture.Store.Read();
        var reorder = new AutomationChanges(catalog.Revision, [], [], [], [],
            catalog.Queues.Select(queue => queue with { Order = 1 - queue.Order }).ToImmutableArray(), [], []);
        fixture.Store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", reorder), reorder, DateTimeOffset.UtcNow);
        var reopened = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "diagnostics"), new JournalStartupAdmission(fixture.Instance, new()));
        catalog = new AutomationDefinitionStore(reopened).Read();
        Assert.Equal(new[] { first.Id, second.Id }, catalog.Queues.Select(queue => queue.Id));
        var codec = new AutomationBundleCodec(new());
        var decoded = codec.Decode(codec.Encode(catalog, DateTimeOffset.UtcNow), catalog.Roots.ToDictionary(root => root.Id, root => root.Path), 0);
        var imported = AutomationEdits.Apply(AutomationCatalog.Empty, decoded.Definitions);
        Assert.Equal(catalog.Queues.Select(queue => (queue.Id, queue.Order)), imported.Queues.Select(queue => (queue.Id, queue.Order)));
        Assert.Throws<AutomationConflictException>(() => fixture.Store.Commit(
            AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", reorder), reorder, DateTimeOffset.UtcNow));
    }

    [Theory(DisplayName = "Script Finish Update Preference Survives Independent Open And New Format Export Import")]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesFinishPreference(bool updateAtFinish)
    {
        var fixture = Create();
        var initial = AutomationDefinitionSeed.Create(fixture.Root);
        initial = initial with { Scripts = [initial.Scripts[0] with { UpdateConfigurationAtFinish = updateAtFinish }] };
        fixture.Store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", initial), initial, DateTimeOffset.UtcNow);
        var reopened = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "diagnostics"),
            new JournalStartupAdmission(fixture.Instance, new()));
        var current = new AutomationDefinitionStore(reopened).Read();
        Assert.Equal(updateAtFinish, Assert.Single(current.Scripts).UpdateConfigurationAtFinish);
        var codec = new AutomationBundleCodec(new());
        var bytes = codec.Encode(current, DateTimeOffset.UtcNow);
        var decoded = codec.Decode(bytes, current.Roots.ToDictionary(root => root.Id, root => root.Path), 0);
        var other = StateDatabase.CreateFresh(new InstanceRoot(new()).OpenOrCreate(Path.Combine(fixture.Root, "other-runtime")));
        var otherStore = new AutomationDefinitionStore(other);
        otherStore.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", decoded.Definitions), decoded.Definitions, DateTimeOffset.UtcNow);
        Assert.Equal(updateAtFinish, Assert.Single(otherStore.Read().Scripts).UpdateConfigurationAtFinish);
    }

    [Fact(DisplayName = "User Order Survives Atomic Reordering Independent Open And New Format Export Import")]
    public void PersistsUserOrder()
    {
        var fixture = Create();
        var initial = AutomationDefinitionSeed.Create(fixture.Root);
        fixture.Store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", initial), initial, DateTimeOffset.UtcNow);
        var before = fixture.Store.Read();
        var edits = new AutomationChanges(before.Revision, [], [],
            [before.Users[0] with { Order = 2 }, before.Users[1] with { Order = 0 }], [], [], [], []);
        var operation = AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", edits);
        fixture.Store.Commit(operation, edits, DateTimeOffset.UtcNow);
        var reopened = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "diagnostics"),
            new JournalStartupAdmission(fixture.Instance, new()));
        var current = new AutomationDefinitionStore(reopened).Read();
        Assert.Equal(new[] { before.Users[1].Id, before.Users[0].Id }, current.Users.Select(user => user.Id));
        Assert.Equal(new[] { 0, 2 }, current.Users.Select(user => user.Order));
        Assert.All(current.Users, user => Assert.Equal(2, user.Revision));
        Assert.Equal(before.Queues[0].ScriptIds.ToArray(), current.Queues[0].ScriptIds.ToArray());
        var codec = new AutomationBundleCodec(new());
        var bytes = codec.Encode(current, DateTimeOffset.UtcNow);
        var decoded = codec.Decode(bytes, current.Roots.ToDictionary(root => root.Id, root => root.Path), 0);
        var other = StateDatabase.CreateFresh(new InstanceRoot(new()).OpenOrCreate(Path.Combine(fixture.Root, "other-runtime")));
        var otherStore = new AutomationDefinitionStore(other);
        otherStore.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", decoded.Definitions), decoded.Definitions, DateTimeOffset.UtcNow);
        Assert.Equal(current.Users.Select(user => (user.Id, user.Order)), otherStore.Read().Users.Select(user => (user.Id, user.Order)));
        Assert.False(fixture.Store.Commit(operation, edits, DateTimeOffset.UtcNow).IsNew);
        Assert.Equal(current.Users.ToArray(), fixture.Store.Read().Users.ToArray());
    }

    [Theory(DisplayName = "Invalid User Order Cannot Consume Operation Or Change Any Definition")]
    [InlineData(-1)]
    [InlineData(4096)]
    public void RejectsInvalidOrder(int order)
    {
        var fixture = Create();
        var initial = AutomationDefinitionSeed.Create(fixture.Root);
        var request = initial with { Users = [initial.Users[0] with { Order = order }, initial.Users[1]] };
        var operationId = Guid.NewGuid();
        Assert.Equal("automation_user_invalid", Assert.Throws<AutomationDefinitionException>(() =>
            fixture.Store.Commit(AutomationDefinitionStore.Identity(operationId, "fixture", request), request, DateTimeOffset.UtcNow)).Message);
        Assert.Equal(0, fixture.Store.Read().Revision);
        Assert.True(fixture.Store.Commit(AutomationDefinitionStore.Identity(operationId, "fixture", initial), initial, DateTimeOffset.UtcNow).IsNew);
    }

    [Fact(DisplayName = "Missing Required User Order Is Rejected Without Publishing Definitions")]
    public void RequiresUserOrder()
    {
        var fixture = Create();
        var initial = AutomationDefinitionSeed.Create(fixture.Root);
        var catalog = AutomationEdits.Apply(AutomationCatalog.Empty, initial);
        var codec = new AutomationBundleCodec(new());
        var wire = JsonNode.Parse(codec.Encode(catalog, DateTimeOffset.UtcNow))!;
        wire["users"]![0]!.AsObject().Remove("order");
        Assert.Throws<System.Text.Json.JsonException>(() => codec.Decode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(wire),
            catalog.Roots.ToDictionary(root => root.Id, root => root.Path), 0));
        Assert.Equal(0, fixture.Store.Read().Revision);
    }

    private static Fixture Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "user-order-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        return new(root, instance, new(database));
    }

    private sealed record Fixture(string Root, RuntimeInstance Instance, AutomationDefinitionStore Store);
}
