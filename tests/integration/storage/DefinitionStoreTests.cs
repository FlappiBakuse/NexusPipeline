using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Records;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Host.Control.Automation;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DefinitionStoreTests
{
    [Theory(DisplayName = "Effective Binding Display Frozen Plan Validation And Notification Routes Share Every Override Combination")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task ConsumesEffectiveOverrides(int groups)
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var definitions = new AutomationDefinitionService(source.Instance, new(), source.Database, source.Lease, fixture.Resources, fixture.Index);
        var useCases = new AutomationUseCases(definitions, new(new()));
        var authority = new AuthenticatedControlRequest(ControlOrigin.Local, "override-fixture", null, () => new(false, false));
        var rawTasks = ImmutableArray.Create(Guid.NewGuid(), Guid.NewGuid());
        var overrideTasks = ImmutableArray.Create(Guid.NewGuid(), rawTasks[0]);
        var notifications = new NotificationCatalogService(source.Instance, source.Database, source.Lease, fixture.Resources, fixture.Index, new());
        foreach (var (id, index) in rawTasks.Append(overrideTasks[0]).Select((id, index) => (id, index)))
            _ = await notifications.SaveAsync(Guid.NewGuid(), "override-fixture", index, id, 0,
                new("Override task " + index, "", NotificationTaskKind.Webhook, false, NotificationChannelConfiguration.Default), []);
        var general = (groups & 1) != 0; var notification = (groups & 2) != 0; var advanced = (groups & 4) != 0;
        foreach (var variant in Enumerable.Range(0, 4))
        {
            var catalog = source.Definitions.Read();
            var raw = catalog.Bindings.Single(item => item.Id == source.OtherBinding);
            var script = catalog.Scripts.Single(item => item.Id == raw.ScriptId);
            var hook = new AutomationHook(new(script.RootId, Path.GetFileName(source.OtherImage)), ["raw", "two words"]);
            raw = raw with { IsEnabled = true, RunDays = 7, MaximumSuccessfulRunsPerDay = 3,
                NotifyEnabled = true, NotificationTaskIds = rawTasks, PreHook = hook, PostHook = hook,
                PreOnceAfterSuccess = false, PostFinalOnly = true };
            var days = new[] { -1, 0, 36500, -1 }[variant]; var limit = new[] { -1, 1, 9999, -1 }[variant];
            var selectedTasks = variant < 2 ? ImmutableArray<Guid>.Empty : overrideTasks;
            var selectedHook = variant % 2 == 0 ? null : hook with { Arguments = ["override"] };
            var user = catalog.Users.Single(item => item.Id == raw.UserId) with { BindingOverrides = new()
            {
                General = new(general, variant != 0, days, limit), Notification = new(notification, variant % 2 != 0, selectedTasks),
                Advanced = new(advanced, selectedHook, selectedHook, selectedHook is not null, false)
            } };
            var scripts = catalog.Scripts.Select(item => item with { Limits = item.Limits with { MaximumAttempts = 1 } }).ToImmutableArray();
            var changes = new AutomationChanges(catalog.Revision, [], scripts, [user], [raw], [], [], []);
            source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "override-fixture", changes), changes, PlanBuilderFixture.CreatedAt);
            var displayed = System.Text.Json.JsonSerializer.SerializeToElement(useCases.BindingEffective(authority, raw.Id),
                System.Text.Json.JsonSerializerOptions.Web);
            var effective = displayed.GetProperty("effective");
            Assert.Equal(!general || variant != 0, effective.GetProperty("isEnabled").GetBoolean());
            Assert.Equal(general ? days : 7, effective.GetProperty("runDays").GetInt32());
            Assert.Equal(general ? limit : 3, effective.GetProperty("maximumSuccessfulRunsPerDay").GetInt32());
            Assert.Equal(!notification || variant % 2 != 0, effective.GetProperty("notifyEnabled").GetBoolean());
            Assert.Equal((notification ? selectedTasks : rawTasks).ToArray(),
                effective.GetProperty("notificationTaskIds").EnumerateArray().Select(item => item.GetGuid()).ToArray());
            Assert.Equal(advanced && selectedHook is null, effective.GetProperty("preHook").ValueKind == System.Text.Json.JsonValueKind.Null);
            Assert.Equal(advanced && selectedHook is not null, effective.GetProperty("preOnceAfterSuccess").GetBoolean());
            Assert.Equal(!advanced, effective.GetProperty("postFinalOnly").GetBoolean());
            using var draft = source.Prepare(new QueueTarget(source.QueueId));
            Assert.True(draft.Explanation.HasRunnable, string.Join(",", draft.Explanation.Items.Select(item => item.ReasonCode)));
            using var prepared = source.Builder.Freeze(draft);
            var plan = prepared.Plan.ReadWire();
            var bundle = NexusPipeline.Platform.Storage.Notifications.ExecutionNotificationSource.ReadBundle(source.Instance, new(),
                new(plan.DefinitionBundle.RelativePath, plan.DefinitionBundle.SizeBytes, plan.DefinitionBundle.Sha256));
            ExecutionPlanSources.Validate(plan, bundle);
            var step = plan.Steps.SingleOrDefault(item => item.BindingId == raw.Id.ToString("D"));
            if (general && variant < 2) Assert.Null(step);
            else
            {
                Assert.NotNull(step); Assert.Equal(general ? limit : 3, step.MaximumSuccessfulRunsPerDay);
                Assert.Equal(advanced && selectedHook is null ? 0 : 2, (step.Hooks ?? []).Count);
                if (step.Hooks is { Count: > 0 } hooks)
                {
                    Assert.Equal(advanced ? new[] { "override" } : new[] { "raw", "two words" }, hooks[0].SourceArguments);
                    Assert.Equal(advanced, hooks[0].OnceAfterSuccess); Assert.Equal(!advanced, hooks[1].FinalOnly);
                }
                var damaged = plan with { Steps = plan.Steps.Select(item => item == step ? item with
                    { MaximumSuccessfulRunsPerDay = 2 } : item).ToArray() };
                Assert.Equal("execution_daily_limit_unbound", Assert.Throws<IOException>(() => ExecutionPlanSources.Validate(damaged, bundle)).Message);
            }
            var routes = NexusPipeline.Platform.Storage.Notifications.ExecutionNotificationSource.ReadRoutes(plan, bundle);
            var route = routes.SingleOrDefault(item => item.SourceId == raw.Id);
            if (general && variant == 0 || notification && variant % 2 == 0) Assert.Null(route);
            else
            {
                Assert.NotNull(route); Assert.Equal(NexusPipeline.Core.Notifications.NotificationRoutingKind.Binding, route.Kind);
                Assert.Equal((notification ? selectedTasks : rawTasks).ToArray(), route.Selection.TaskIds.ToArray());
            }
            var savedRaw = source.Definitions.Read().Bindings.Single(item => item.Id == raw.Id);
            Assert.Equal(7, savedRaw.RunDays); Assert.Equal(3, savedRaw.MaximumSuccessfulRunsPerDay);
            Assert.Equal(rawTasks.ToArray(), savedRaw.NotificationTaskIds.ToArray()); Assert.NotNull(savedRaw.PreHook);
        }
    }

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
        Sql(fixture.Database, "CREATE TRIGGER automation.fixture_fail_queue BEFORE UPDATE ON queues BEGIN SELECT RAISE(ABORT,'fixture_write_failure'); END;");
        try
        {
            Assert.Throws<SqliteException>(() => fixture.Store.Commit(operation, request, DateTimeOffset.UtcNow));
            Assert.Equal(before, BoundedJson.Serialize(fixture.Store.Read()));
        }
        finally { Sql(fixture.Database, "DROP TRIGGER automation.fixture_fail_queue;"); }
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

    [Fact(DisplayName = "Script And Queue Edits Keep Repeated Ordinals While Script Removal Updates Only Its Queue Members")]
    public void PreservesRepeatedQueueMembersAcrossEdits()
    {
        var fixture = Create();
        var initial = Definitions(fixture.Root);
        var original = Assert.Single(initial.Scripts);
        var removed = original with { Id = Guid.NewGuid(), Name = "Remove later" };
        var updatedQueue = Assert.Single(initial.Queues) with
        {
            ScriptIds = [original.Id, removed.Id, original.Id, removed.Id, original.Id]
        };
        var unaffectedQueue = updatedQueue with { Id = Guid.NewGuid(), Name = "Unchanged queue", ScriptIds = [original.Id, original.Id] };
        initial = initial with { Scripts = [original, removed], Queues = [updatedQueue, unaffectedQueue] };
        fixture.Store.Commit(Identity(initial), initial, DateTimeOffset.UtcNow);

        var current = fixture.Store.Read();
        var script = current.Scripts.Single(item => item.Id == original.Id);
        var queue = current.Queues.Single(item => item.Id == updatedQueue.Id);
        var queueOrder = new[] { removed.Id, original.Id, removed.Id, original.Id };
        File.WriteAllBytes(Path.Combine(current.Roots.Single().Path, "second.exe"), [1, 2, 3]);
        var change = Empty(current.Revision) with
        {
            Scripts = [script with { Name = "Other path", Executable = new(script.RootId, "second.exe") }],
            Queues = [queue with { ScriptIds = queueOrder.ToImmutableArray() }]
        };
        fixture.Store.Commit(Identity(change), change, DateTimeOffset.UtcNow);
        var reordered = fixture.Store.Read();
        Assert.Equal(queueOrder, reordered.Queues.Single(item => item.Id == queue.Id).ScriptIds.ToArray());
        Assert.Equal("second.exe", reordered.Scripts.Single(item => item.Id == script.Id).Executable.RelativePath);
        Assert.Equal(new[] { original.Id, original.Id }, reordered.Queues.Single(item => item.Id == unaffectedQueue.Id).ScriptIds.ToArray());

        var deleted = reordered.Scripts.Single(item => item.Id == removed.Id);
        var deletion = Empty(reordered.Revision) with
        {
            Removed = [new(AutomationDefinitionKind.Script, deleted.Id, deleted.Revision)]
        };
        var operation = Identity(deletion);
        var accepted = fixture.Store.Commit(operation, deletion, DateTimeOffset.UtcNow);
        Assert.True(accepted.IsNew);
        Assert.Equal(accepted.CatalogRevision, fixture.Store.Commit(operation, deletion, DateTimeOffset.UtcNow).CatalogRevision);
        var after = fixture.Store.Read();
        Assert.Equal(original.Id, Assert.Single(after.Scripts).Id);
        var changed = after.Queues.Single(item => item.Id == updatedQueue.Id);
        Assert.Equal(queue.Revision + 2, changed.Revision);
        Assert.Equal(new[] { original.Id, original.Id }, changed.ScriptIds.ToArray());
        var unaffected = after.Queues.Single(item => item.Id == unaffectedQueue.Id);
        Assert.Equal(1L, unaffected.Revision);
        Assert.Equal(new[] { original.Id, original.Id }, unaffected.ScriptIds.ToArray());
        Assert.Equal(updatedQueue.Id, Assert.Single(after.Schedules).QueueId);
    }

    [Fact(DisplayName = "Unknown Persisted Queue Order Is Rejected During Copied Startup Inspection Without Repairing Original Bytes")]
    public void PreservesUnknownOrder()
    {
        var fixture = Create();
        var initial = Definitions(fixture.Root);
        fixture.Store.Commit(Identity(initial), initial, DateTimeOffset.UtcNow);
        Sql(fixture.Database, "UPDATE automation.queue_items SET ordinal=99 WHERE ordinal=0;");
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
        using var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open($"Data Source={database.DatabasePath};Pooling=False");
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
