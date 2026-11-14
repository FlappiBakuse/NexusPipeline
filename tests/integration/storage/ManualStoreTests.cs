using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ManualStoreTests
{
    [Theory(DisplayName = "Manual Acceptance Persists Its Actual Caller Task And Run Without A Schedule And Never Rewinds An Original Receipt")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistsBoundFacts(bool begin)
    {
        using var fixture = new Fixture(); var observed = await fixture.ObserveAsync();
        var operation = Guid.NewGuid(); var client = Guid.NewGuid(); var started = DateTimeOffset.UtcNow;
        using var work = fixture.Store.Accept(observed, operation, "fixture:caller", client, started,
            fixture.Budget(started), () => { });
        var state = fixture.Store.Lookup(operation, "fixture:caller", "fixture", "tasks", fixture.TaskId, 7)!;
        Assert.Equal(client, state.Source.Document.ClientId); Assert.Empty(state.Source.Document.Task.ScheduledTaskIds);
        Assert.Empty(fixture.Registry.PublishedPlanningEntries());
        Assert.True(work.Source.Document.Invocation.Matches(state.Source.Document.Invocation));
        Assert.Throws<OperationConflictException>(() => fixture.Store.Lookup(operation, "foreign", "fixture", "tasks", fixture.TaskId, 7));
        Assert.Throws<OperationConflictException>(() => fixture.Store.Lookup(operation, "fixture:caller", "fixture", "tasks", fixture.TaskId, 8));
        Assert.False(fixture.Store.ReconcilePriorOwners());
        var before = File.ReadAllBytes(fixture.Catalog.Database.DatabasePath);
        Assert.Equal("manual_work_owner_unconfirmed", Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Catalog.Instance,
            Path.Combine(fixture.Catalog.Instance.RuntimeRoot, "live-inspection"), new JournalStartupAdmission(fixture.Catalog.Instance, new()))).Message);
        Assert.Equal(before, File.ReadAllBytes(fixture.Catalog.Database.DatabasePath));
        if (begin)
        {
            fixture.Store.Begin(work);
            Assert.Equal("manual_work_already_dispatched", Assert.Throws<IOException>(() => fixture.Store.Begin(work)).Message);
        }
        var result = fixture.Store.Complete(work, new(BusinessOutcome.Succeeded, "fixture_succeeded"), "fixture_returned");
        Assert.Equal(begin ? "returned" : "notStarted", result.Result!.Completion);
        Assert.Equal(begin ? BusinessOutcome.Succeeded : (BusinessOutcome?)null, result.Result.Returned?.Business);
        var replay = fixture.Store.Complete(work, new(BusinessOutcome.Failed, "fixture_other"), "fixture_other");
        Assert.Equal(result.Result, replay.Result); Assert.Equal(result.ResultReference, replay.ResultReference);
        Assert.True(fixture.Store.ReconcilePriorOwners());
        ManualWorkInventory.Visit(fixture.Catalog.Instance, fixture.Catalog.Database, new(), saved => Assert.Equal(result.ResultReference, saved.ResultReference));
        _ = StateDatabase.OpenExisting(fixture.Catalog.Instance, Path.Combine(fixture.Catalog.Instance.RuntimeRoot, "completed-inspection"),
            new JournalStartupAdmission(fixture.Catalog.Instance, new()));
        Assert.Equal(0, fixture.Source.Calls);
    }

    [Fact(DisplayName = "Revocation At The Manual Commit Boundary Leaves No Accepted Operation Or Run")]
    public async Task RejectsRevokedAcceptance()
    {
        using var fixture = new Fixture(); var observed = await fixture.ObserveAsync(); var operation = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow; var checks = 0;
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Store.Accept(observed, operation, "fixture:caller", Guid.NewGuid(), started,
            fixture.Budget(started), () => { if (++checks == 3) throw new UnauthorizedAccessException("fixture_revoked"); }));
        Assert.Null(fixture.Store.Lookup(operation, "fixture:caller", "fixture", "tasks", fixture.TaskId, 7));
        Assert.NotNull(fixture.Catalog.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
        var count = 0;
        ManualWorkInventory.Visit(fixture.Catalog.Instance, fixture.Catalog.Database, new(), _ => count++);
        Assert.Equal(0, count); Assert.Equal(0, fixture.Source.Calls);
    }

    [Fact(DisplayName = "A Task Quiescence Hold Rejects New Durable Manual Acceptance Before Any Run Or Callback Exists")]
    public async Task QuiescesBeforeAcceptance()
    {
        using var fixture = new Fixture(); var observed = await fixture.ObserveAsync(); var operation = Guid.NewGuid();
        using var hold = await observed.Entry.QuiesceAsync([fixture.TaskId]); var started = DateTimeOffset.UtcNow;
        Assert.Equal("work_task_quiescing", Assert.Throws<InvalidOperationException>(() => fixture.Store.Accept(observed, operation,
            "fixture:caller", Guid.NewGuid(), started, fixture.Budget(started), () => { })).Message);
        Assert.Null(fixture.Store.Lookup(operation, "fixture:caller", "fixture", "tasks", fixture.TaskId, 7));
        Assert.Equal(0, fixture.Source.Calls);
    }

    [Fact(DisplayName = "A Rebound Immutable Manual Source Cannot Replace The Accepted Writer Or Client Facts Even When Its Business Request Is Identical")]
    public async Task RejectsReboundSource()
    {
        using var fixture = new Fixture(); var observed = await fixture.ObserveAsync(); var operation = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        using var work = fixture.Store.Accept(observed, operation, "fixture:caller", Guid.NewGuid(), started, fixture.Budget(started), () => { });
        var changed = work.Source.Document with { ClientId = Guid.NewGuid() };
        var replacement = new ManualWorkCodec(fixture.Catalog.Instance, new()).Persist(changed);
        fixture.Catalog.Database.Write((connection, transaction) =>
        {
            using var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE main.manual_work_runs SET relative_path=$path,size_bytes=$size,sha256=$sha WHERE operation_id=$id;";
            update.Parameters.AddWithValue("$path", replacement.RelativePath); update.Parameters.AddWithValue("$size", replacement.SizeBytes);
            update.Parameters.AddWithValue("$sha", replacement.Sha256); update.Parameters.AddWithValue("$id", operation.ToString("D"));
            return update.ExecuteNonQuery();
        });
        var before = File.ReadAllBytes(fixture.Catalog.Database.DatabasePath);
        Assert.Throws<OperationConflictException>(() => fixture.Store.Lookup(operation, "fixture:caller", "fixture", "tasks", fixture.TaskId, 7));
        Assert.Throws<OperationConflictException>(() => StateDatabase.OpenExisting(fixture.Catalog.Instance,
            Path.Combine(fixture.Catalog.Instance.RuntimeRoot, "rebound-inspection"), new JournalStartupAdmission(fixture.Catalog.Instance, new())));
        Assert.Equal(before, File.ReadAllBytes(fixture.Catalog.Database.DatabasePath));
        Assert.Equal(0, fixture.Source.Calls);
    }

    private sealed class Fixture : IDisposable
    {
        internal ExtensionCatalogFixture Catalog { get; } = new();
        internal Source Source { get; } = new();
        internal Guid TaskId => Source.TaskId;
        internal ScheduledWorkRegistry Registry { get; }
        internal ManualWorkStore Store { get; }
        private readonly IDisposable _registration;
        internal Fixture()
        {
            Registry = new(new Policy()); var activation = new ExtensionActivation("fixture"); activation.Publish();
            _registration = Registry.Register(activation, "tasks", Source);
            Store = new(Catalog.Instance, Catalog.Database, Catalog.Lease, Catalog.Resources, new(), TimeProvider.System);
        }
        internal async Task<ObservedManualTask> ObserveAsync() => (await Registry.RequireManualEntry("fixture", "tasks").ReadManualAsync(TaskId))!;
        internal ExecutionTimeBudget Budget(DateTimeOffset started) => new(TimeProvider.System, TimeProvider.System.GetTimestamp(), TimeSpan.FromMinutes(1), started.AddMinutes(1));
        public void Dispose() { _registration.Dispose(); Catalog.Dispose(); }
    }
    private sealed class Policy : IScheduledInvocationAuthorization
    {
        public void AssertAllowed(FrozenScheduledInvocation invocation, string extensionId, string providerId) => throw new UnauthorizedAccessException();
    }
    private sealed class Source : IManualWorkSource
    {
        internal Guid TaskId { get; } = Guid.NewGuid(); internal int Calls;
        public ValueTask<ManualWorkTask?> ReadTaskAsync(Guid taskId, CancellationToken cancellation) =>
            ValueTask.FromResult<ManualWorkTask?>(taskId == TaskId ? new(TaskId, 7, true, "UTC", 60, [], []) : null);
        public ValueTask<ScheduledBusinessResult> ExecuteAsync(FrozenScheduledInvocation invocation, CancellationToken cancellation) => throw new InvalidOperationException();
        public ValueTask<ScheduledBusinessResult> ExecuteManualAsync(ManualInvocation invocation, CancellationToken cancellation)
        { Calls++; throw new InvalidOperationException("fixture_callback_forbidden"); }
    }
}
