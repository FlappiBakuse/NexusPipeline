using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
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
        var codec = new AutomationBundleCodec(new());
        var parsed = codec.Decode(codec.Encode(catalog, DateTimeOffset.UtcNow), catalog.Roots.ToDictionary(item => item.Id, item => item.Path), 0);
        var roundTrip = AutomationEdits.Apply(AutomationCatalog.Empty, parsed.Definitions);
        Assert.Equal(catalog.Users.OrderBy(item => item.Id).Select(item => (item.Id, item.Notes, item.Avatar)), roundTrip.Users.OrderBy(item => item.Id).Select(item => (item.Id, item.Notes, item.Avatar)));
        Assert.Equal(catalog.Scripts.OrderBy(item => item.Id).Select(item => (item.Id, item.Order)), roundTrip.Scripts.OrderBy(item => item.Id).Select(item => (item.Id, item.Order)));
        Assert.Equal(catalog.Schedules.OrderBy(item => item.Id).Select(item => (item.Id, item.Order)), roundTrip.Schedules.OrderBy(item => item.Id).Select(item => (item.Id, item.Order)));
        Assert.False(roundTrip.Queues[0].ScheduleEnabled);
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
