using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Host.Control.Automation;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DefinitionStoreTests
{
    [Fact(DisplayName = "Effective Binding Projection Uses One Saved Catalog And Names Every Override Source")]
    public void ProjectsEffectiveBinding()
    {
        var fixture = Create();
        var request = Definitions(fixture.Root);
        var user = request.Users.Single(item => item.Id == request.Bindings[0].UserId) with { BindingOverrides = new()
        {
            General = new(true, false, 4, 2), Notification = new(false, true, []),
            Advanced = new(false, null, null, false, false)
        } };
        request = request with { Users = request.Users.Select(item => item.Id == user.Id ? user : item).ToImmutableArray(),
            Bindings = request.Bindings.SetItem(0, request.Bindings[0] with { IsEnabled = true }) };
        fixture.Store.Commit(Identity(request), request, DateTimeOffset.UtcNow);
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var index = new RecoveryIndex(); index.PublishVerified(0, 1, []);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        var definitions = new AutomationDefinitionService(fixture.Instance, new(), fixture.Database, lease, resources, index);
        var useCases = new AutomationUseCases(definitions, new(new()));
        var authority = new AuthenticatedControlRequest(ControlOrigin.Local, "fixture", null,
            () => new ControlPermissionSnapshot(false, false));
        var effective = System.Text.Json.JsonSerializer.SerializeToElement(
            useCases.BindingEffective(authority, request.Bindings[0].Id),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(1, effective.GetProperty("catalogRevision").GetInt64());
        Assert.True(effective.GetProperty("raw").GetProperty("isEnabled").GetBoolean());
        Assert.False(effective.GetProperty("effective").GetProperty("isEnabled").GetBoolean());
        Assert.Equal(4, effective.GetProperty("effective").GetProperty("runDays").GetInt32());
        Assert.Equal("user", effective.GetProperty("sources").GetProperty("isEnabled").GetString());
        Assert.Equal("binding", effective.GetProperty("sources").GetProperty("notifyEnabled").GetString());
    }
    [Theory(DisplayName = "Script Identity Changes Cannot Commit Or Consume The Operation Used For A Corrected Ordinary Edit")]
    [InlineData("generic-to-specialized")]
    [InlineData("specialized-to-generic")]
    [InlineData("another-extension")]
    public void PreservesScriptIdentity(string change)
    {
        var fixture = Create();
        var initial = Definitions(fixture.Root);
        if (change != "generic-to-specialized")
            initial = initial with { Scripts = [initial.Scripts[0] with { Kind = AutomationKind.Specialized, ExtensionId = "first-extension" }] };
        fixture.Store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", initial), initial, DateTimeOffset.UtcNow);
        var catalog = fixture.Store.Read();
        var original = catalog.Scripts[0];
        var changed = change == "specialized-to-generic" ? original with { Kind = AutomationKind.Generic, ExtensionId = null }
            : original with { Kind = AutomationKind.Specialized, ExtensionId = "another-extension" };
        var edits = new AutomationChanges(catalog.Revision, [], [changed], [], [], [], [], []);
        var operationId = Guid.NewGuid();
        Assert.Equal("automation_script_identity_changed", Assert.Throws<AutomationDefinitionException>(() =>
            fixture.Store.Commit(AutomationDefinitionStore.Identity(operationId, "fixture", edits), edits, DateTimeOffset.UtcNow)).Message);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(catalog), System.Text.Json.JsonSerializer.Serialize(fixture.Store.Read()));
        var corrected = edits with { Scripts = [original with { Name = "Edited" }] };
        Assert.Equal(2, fixture.Store.Commit(AutomationDefinitionStore.Identity(operationId, "fixture", corrected), corrected, DateTimeOffset.UtcNow).CatalogRevision);
    }

    [Fact(DisplayName = "Changing A Bindings Account Cannot Reuse Its Stable Id Or Consume The Rejected Operation")]
    public void PreservesBindingIdentity()
    {
        var fixture = Create();
        var initial = Definitions(fixture.Root);
        fixture.Store.Commit(Identity(initial), initial, DateTimeOffset.UtcNow);
        var current = fixture.Store.Read();
        var binding = current.Bindings[0];
        var edits = Empty(current.Revision) with { Bindings = [binding with { UserId = Guid.NewGuid() }] };
        var operation = Identity(edits);
        var before = BoundedJson.Serialize(current);
        Assert.Equal("automation_binding_identity_changed", Assert.Throws<AutomationDefinitionException>(() =>
            fixture.Store.Commit(operation, edits, DateTimeOffset.UtcNow)).Message);
        Assert.Equal(before, BoundedJson.Serialize(fixture.Store.Read()));
        var corrected = Empty(current.Revision) with { Bindings = [binding with { IsEnabled = true }] };
        Assert.True(fixture.Store.Commit(AutomationDefinitionStore.Identity(operation.OperationId, "fixture", corrected), corrected, DateTimeOffset.UtcNow).IsNew);
    }

    [Fact(DisplayName = "A Complete Definition Graph Survives An Independent Database Open With Its Exact Queue Order")]
    public void ReopensDefinitionGraph()
    {
        var fixture = Create();
        var request = Definitions(fixture.Root);
        var result = fixture.Store.Commit(Identity(request), request, DateTimeOffset.UtcNow);
        Assert.True(result.IsNew);
        Assert.Equal(1, result.CatalogRevision);
        var reopened = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "diagnostics"), new JournalStartupAdmission(fixture.Instance, new()));
        var current = new AutomationDefinitionStore(reopened).Read();
        Assert.Equal(request.Roots[0].Path, Assert.Single(current.Roots).Path);
        Assert.Equal("--fixture", Assert.Single(current.Scripts).Arguments.Single());
        Assert.Equal(2, current.Users.Length);
        Assert.Equal(2, current.Bindings.Length);
        Assert.Equal(request.Queues[0].ScriptIds.ToArray(), Assert.Single(current.Queues).ScriptIds.ToArray());
        Assert.All(current.Bindings, value => Assert.Equal(1, value.Revision));
        Assert.Equal("UTC", Assert.Single(current.Schedules).TimeZoneId);
    }

    [Fact(DisplayName = "Replaying An Accepted Definition Change Keeps Its Original Revision And Never Discloses It To Another Subject")]
    public void BindsAcceptedReplay()
    {
        var fixture = Create();
        var initial = Definitions(fixture.Root);
        var operation = Identity(initial);
        fixture.Store.Commit(operation, initial, DateTimeOffset.UtcNow);
        var current = fixture.Store.Read();
        var renamed = Empty(current.Revision) with { Users = [current.Users[0] with { Name = "New name" }] };
        fixture.Store.Commit(Identity(renamed), renamed, DateTimeOffset.UtcNow);
        var replay = fixture.Store.Commit(operation, initial, DateTimeOffset.UtcNow);
        Assert.False(replay.IsNew);
        Assert.Equal(1, replay.CatalogRevision);
        Assert.Equal(2, fixture.Store.Read().Revision);
        Assert.Contains(fixture.Store.Read().Users, value => value.Name == "New name");
        var other = new OperationIdentity(operation.OperationId, "another-subject", operation.RequestDigest, operation.Kind);
        Assert.Throws<OperationConflictException>(() => fixture.Store.Commit(other, initial, DateTimeOffset.UtcNow));
        Assert.Equal(2, fixture.Store.Read().Revision);
    }

    [Fact(DisplayName = "Independent Sqlite Writers Cannot Both Commit The Same Catalog Revision Or Consume The Rejected Identity")]
    public async Task SerializesCatalogWriters()
    {
        var fixture = Create();
        var initial = Definitions(fixture.Root);
        fixture.Store.Commit(Identity(initial), initial, DateTimeOffset.UtcNow);
        var current = fixture.Store.Read();
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "concurrent-inspection"));
        var stores = new[] { fixture.Store, new AutomationDefinitionStore(database) };
        var edits = Enumerable.Range(0, 2).Select(index => Empty(current.Revision) with
            { Users = [current.Users[0] with { Name = "Writer " + index }] }).ToArray();
        var operations = edits.Select(value => Identity(value)).ToArray();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run<Exception?>(() =>
        {
            try { stores[index].Commit(operations[index], edits[index], DateTimeOffset.UtcNow); return null; }
            catch (Exception error) { return error; }
        })));
        Assert.Single(outcomes, value => value is null);
        Assert.IsType<AutomationConflictException>(Assert.Single(outcomes, value => value is not null));
        var rejected = System.Array.FindIndex(outcomes, value => value is not null);
        var after = fixture.Store.Read();
        var retry = Empty(after.Revision) with { Users = [after.Users.Single(value => value.Id == current.Users[0].Id) with { Name = "Retry after conflict" }] };
        var accepted = fixture.Store.Commit(AutomationDefinitionStore.Identity(operations[rejected].OperationId, "fixture", retry), retry, DateTimeOffset.UtcNow);
        Assert.True(accepted.IsNew);
        Assert.Equal(3, accepted.CatalogRevision);
    }

    [Fact(DisplayName = "An Actual Sqlite Failure After Earlier Row Writes Rolls Back The Whole Definition Change And Its Operation")]
    public void RevertsSqlFailure()
    {
        var fixture = Create();
        var initial = Definitions(fixture.Root);
        fixture.Store.Commit(Identity(initial), initial, DateTimeOffset.UtcNow);
        var current = fixture.Store.Read();
        var request = Empty(current.Revision) with { Users = [current.Users[0] with { Name = "Must roll back" }],
            Queues = [current.Queues[0] with { Name = "Failing queue update" }] };
        var operation = Identity(request);
        var before = BoundedJson.Serialize(current);
        Sql(fixture.Database, "CREATE TRIGGER fixture_fail_queue BEFORE UPDATE ON queues BEGIN SELECT RAISE(ABORT,'fixture_write_failure'); END;");
        try
        {
            Assert.Throws<SqliteException>(() => fixture.Store.Commit(operation, request, DateTimeOffset.UtcNow));
            Assert.Equal(before, BoundedJson.Serialize(fixture.Store.Read()));
        }
        finally { Sql(fixture.Database, "DROP TRIGGER fixture_fail_queue;"); }
        Assert.True(fixture.Store.Commit(operation, request, DateTimeOffset.UtcNow).IsNew);
        Assert.Equal(2, fixture.Store.Read().Revision);
    }

    [Fact(DisplayName = "Joint User Deletion Keeps Other Bindings Queue Order And The Immutable Historical Attempt")]
    public void PreservesJointDeletion()
    {
        var fixture = Create();
        var initial = Definitions(fixture.Root);
        fixture.Store.Commit(Identity(initial), initial, DateTimeOffset.UtcNow);
        var current = fixture.Store.Read();
        var user = current.Users[0];
        var removedBinding = current.Bindings.Single(value => value.UserId == user.Id);
        var record = new CompletedRunRecord(current.Scripts[0].Id, user.Id, DateTimeOffset.UtcNow, 123,
            new(Guid.NewGuid(), Guid.NewGuid(), 1, EngineOutcome.Succeeded, BusinessOutcome.Succeeded,
                LifecycleOutcome.Completed, RecoveryOutcome.NotRequired, "fixture_completed"));
        var records = new RunRecordStore(fixture.Database);
        records.AppendCompleted([record]);
        var deletion = Empty(current.Revision) with { Removed = [new(AutomationDefinitionKind.User, user.Id, user.Revision)] };
        fixture.Store.Commit(Identity(deletion), deletion, DateTimeOffset.UtcNow);
        var after = fixture.Store.Read();
        Assert.Single(after.Users);
        Assert.Single(after.Bindings);
        Assert.Equal(current.Queues[0].ScriptIds.ToArray(), after.Queues[0].ScriptIds.ToArray());
        Assert.Equal(current.Queues[0].Revision, after.Queues[0].Revision);
        Assert.Equal(record, Assert.Single(records.ReadPage(new()).Items));
    }

    [Fact(DisplayName = "Unknown Persisted Queue Order Is Rejected During Copied Startup Inspection Without Repairing Original Bytes")]
    public void PreservesUnknownOrder()
    {
        var fixture = Create();
        var initial = Definitions(fixture.Root);
        fixture.Store.Commit(Identity(initial), initial, DateTimeOffset.UtcNow);
        Sql(fixture.Database, "UPDATE queue_items SET ordinal=99 WHERE ordinal=0;");
        var before = Group(fixture.Database);
        Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "unknown-inspection"),
            new JournalStartupAdmission(fixture.Instance, new())));
        Assert.Equal(before, Group(fixture.Database));
    }

    [Theory(DisplayName = "Definition Roots Cannot Claim The Runtime Or Use An Unresolved Traversal")]
    [InlineData("inside")]
    [InlineData("ancestor")]
    [InlineData("noncanonical")]
    public void RejectsUnboundRoots(string fault)
    {
        var fixture = Create();
        var initial = Definitions(fixture.Root);
        var root = initial.Roots[0];
        var path = fault switch
        {
            "inside" => Path.Combine(fixture.Instance.RuntimeRoot, "owned"),
            "ancestor" => fixture.Root,
            _ => Path.Combine(root.Path, "..", "other")
        };
        initial = initial with { Roots = [root with { Path = path }] };
        var before = Group(fixture.Database);
        Assert.Throws<AutomationDefinitionException>(() => fixture.Store.Commit(Identity(initial), initial, DateTimeOffset.UtcNow));
        Assert.Equal(before, Group(fixture.Database));
        Assert.Equal(0, fixture.Store.Read().Revision);
    }

    [Fact(DisplayName = "Oversized Definition Requests Cannot Allocate An Unbounded Serialized Payload Or Commit Rows")]
    public void BoundsDefinitionPayloads()
    {
        var fixture = Create();
        var initial = Definitions(fixture.Root);
        var arguments = Enumerable.Repeat(new string('x', 4096), 128).ToImmutableArray();
        initial = initial with { Scripts = Enumerable.Range(0, 5).Select(index => initial.Scripts[0] with
            { Id = Guid.NewGuid(), Arguments = arguments }).ToImmutableArray(), Bindings = [], Queues = [], Schedules = [] };
        Assert.Throws<System.Text.Json.JsonException>(() => Identity(initial));
        Assert.Equal(0, fixture.Store.Read().Revision);
    }

    private static OperationIdentity Identity(AutomationChanges changes) => AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes);
    private static AutomationChanges Empty(long revision) => new(revision, [], [], [], [], [], [], []);

    private static AutomationChanges Definitions(string root) => AutomationDefinitionSeed.Create(root);

    private static Fixture Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "automation-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        return new(root, instance, database, new(database));
    }

    private static void Sql(StateDatabase database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string[] Group(StateDatabase database) => new[] { "", "-wal", "-shm", "-journal" }
        .Where(suffix => File.Exists(database.DatabasePath + suffix))
        .Select(suffix => suffix + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(database.DatabasePath + suffix)))).ToArray();

    private sealed record Fixture(string Root, RuntimeInstance Instance, StateDatabase Database, AutomationDefinitionStore Store);
}
