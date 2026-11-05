using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Scheduling;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class NotificationScheduledFixture : IAsyncDisposable
{
    internal ExecutionAdmissionFixture Base { get; } = new();
    internal MutableClock Clock { get; } = new();
    internal Guid NotificationTask { get; } = Guid.NewGuid();
    internal Guid TaskId { get; } = Guid.NewGuid();
    internal NotificationRuntime Notifications { get; }
    internal ScheduledProviderDispatch Dispatch { get; }
    internal ScheduleRuntimeService Tick { get; }
    internal ExtensionScope? Scope { get; private set; }
    internal NotificationSubmissionPort? Port { get; private set; }
    internal Func<Task>? AfterPlanSnapshot { get; set; }
    internal Func<Task>? BeforeSubmission { get; set; }
    internal bool ThrowAfterCallbackGate { get; set; }
    internal bool RetrySubmissionAfterSqlFailure { get; set; }
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource CallbackGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<Exception> SubmissionFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource RetrySubmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool HoldCallback { get; set; }
    internal bool SubmitNotification { get; set; } = true;
    internal int ComponentCalls, PlannedCalls;
    internal ScheduledPlannedInvocation? LastContext { get; private set; }
    private string PrivateDatabase => Path.Combine(Base.Source.Root, "scheduled-private.db");
    private readonly int _localMinute;

    internal NotificationScheduledFixture()
    {
        var source = Base.Source;
        _localMinute = Clock.GetUtcNow().Hour * 60 + Clock.GetUtcNow().Minute;
        using (var connection = OpenPrivate(create: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE saved_task(singleton INTEGER PRIMARY KEY,plans_revision INTEGER,task_revision INTEGER,effects INTEGER); INSERT INTO saved_task VALUES(1,3,2,0);";
            command.ExecuteNonQuery();
        }
        Dispatch = new(source.Instance, source.Database, new(), source.Lease, Base.Resources, Clock);
        Notifications = new(source.Instance, source.Database, new(), source.Lease, Base.Resources, Base.Index,
            new(source.Instance, source.Database, source.Lease, Base.Resources, Base.Index, new()));
        Notifications.InitializeStartup();
        Tick = new(new ScheduleOccurrenceStore(source.Instance, source.Database, source.Lease, Base.Resources, Clock),
            new ScheduledExecutionAdmission(source.Instance, source.Database, source.Lease, Base.Service, Clock),
            Clock, lifecycle: Base.Lifecycle, providers: Dispatch);
    }

    internal async Task SaveNotification(Uri address)
    {
        var source = Base.Source;
        var catalog = new NotificationCatalogService(source.Instance, source.Database, source.Lease, Base.Resources, Base.Index, new());
        using var url = new SecretValue(address.AbsoluteUri);
        _ = await catalog.SaveAsync(Guid.NewGuid(), "local:scheduled-native", 0, NotificationTask, 0,
            new("scheduled task", "", NotificationTaskKind.Webhook, true, NotificationChannelConfiguration.Default with {
                WebhookKind = NotificationWebhookKind.Generic, WebhookTemplate = "{\"text\":{text}}" }),
            [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url)]);
    }

    internal async Task Activate()
    {
        var manifest = new ExtensionManifest { Format = "nexus.extension.manifest", Version = 1, ExtensionId = "scheduled-fixture",
            ArtifactName = "ScheduledFixture", DisplayName = "Scheduled fixture", PackageVersion = "0.5.0",
            Kind = "managed-code", MinHostVersion = "0.18.0", SdkVersion = "0.1.0", ContractFingerprint = SdkIdentity.ContractFingerprint,
            Capabilities = ["schedule.register", "notification.submit"], Permissions = new(),
            Managed = new() { EntryAssembly = "ScheduledFixture.dll", EntryType = "Fixture.Extension" } };
        Scope = await new ExtensionActivator(new()).ActivateManagedAsync(JsonSerializer.SerializeToUtf8Bytes(manifest), manifest.ExtensionId,
            (admitted, scope) =>
            {
                var scheduling = new ScheduleRegistrationPort(admitted, scope, Dispatch.Registry, new());
                var backend = new ScheduledNotificationSubmission(Base.Source.Instance, Base.Source.Database, new(), Base.Source.Lease,
                    Dispatch, Base.Resources, scope, (IWorkQueueAcceptance)Notifications);
                Port = new(admitted, scope, backend, backend, new());
                return new Extension(token => scheduling.RegisterAsync("daily", new Handler(this), token));
            }, default);
    }

    internal long Effects => PrivateScalar("SELECT effects FROM saved_task;");
    internal void ChangePrivateTask()
    {
        using var connection = OpenPrivate(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE saved_task SET plans_revision=4,task_revision=3;"; command.ExecuteNonQuery();
    }
    internal IReadOnlyList<ScheduledProviderState> States()
    {
        var result = new List<ScheduledProviderState>();
        ScheduledProviderInventory.Visit(Base.Source.Instance, Base.Source.Database, new(), result.Add); return result;
    }
    internal async Task PumpUntilResult()
    {
        var deadline = TimeProvider.System.GetTimestamp();
        while (States().All(item => item.Result is null))
        {
            if (TimeProvider.System.GetElapsedTime(deadline) > TimeSpan.FromSeconds(20)) throw new TimeoutException("scheduled_fixture_result_missing");
            await Tick.TickAsync(); await Task.Delay(10);
        }
    }

    private SqliteConnection OpenPrivate(bool create = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = PrivateDatabase,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open(); return connection;
    }
    private long PrivateScalar(string sql)
    {
        using var connection = OpenPrivate(); using var command = connection.CreateCommand(); command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
    private ScheduledWorkPlans ReadSavedPlan()
    {
        using var connection = OpenPrivate(); using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT plans_revision,task_revision FROM saved_task WHERE singleton=1;";
        using var row = command.ExecuteReader(); Assert.True(row.Read());
        return new() { Format = "nexus.schedule.work-plans", Version = 1, Revision = row.GetInt64(0),
            Tasks = [new() { TaskId = TaskId.ToString("D"), Revision = row.GetInt64(1), IsEnabled = true,
                TimeZoneId = "UTC", Weekdays = [(long)Clock.Initial.DayOfWeek], LocalMinute = _localMinute,
                TimeoutSeconds = 120, NotificationTaskIds = [NotificationTask.ToString("D")] }] };
    }
    private void AcceptSavedRevision(ScheduledPlannedInvocation invocation)
    {
        using var connection = OpenPrivate(); using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE saved_task SET effects=effects+1 WHERE singleton=1 AND plans_revision=$plans AND task_revision=$task;";
        command.Parameters.AddWithValue("$plans", invocation.PlansRevision); command.Parameters.AddWithValue("$task", invocation.TaskRevision);
        if (command.ExecuteNonQuery() != 1) throw new UnauthorizedAccessException("fixture_saved_task_revision_changed");
        transaction.Commit();
    }

    public async ValueTask DisposeAsync()
    {
        CallbackGate.TrySetResult(); RetrySubmission.TrySetResult();
        try { await Tick.StopAsync(); }
        finally
        {
            try { if (Scope is not null) await Scope.DisposeAsync(); }
            finally { try { await Notifications.DisposeAsync(); } finally { Base.Dispose(); } }
        }
    }

    private sealed class Handler(NotificationScheduledFixture owner) : IScheduledWorkPlanningHandler
    {
        public async ValueTask<ScheduledWorkPlans> ReadPlansAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); var saved = owner.ReadSavedPlan();
            if (owner.AfterPlanSnapshot is { } observe) await observe();
            return saved;
        }
        public ValueTask<ScheduledWorkResult> ExecuteAsync(ScheduledInvocation invocation, CancellationToken cancellationToken)
        { Interlocked.Increment(ref owner.ComponentCalls); throw new IOException("unversioned_fixture_entry_forbidden"); }
        public async ValueTask<ScheduledWorkResult> ExecutePlannedAsync(ScheduledPlannedInvocation context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref owner.PlannedCalls);
            owner.AcceptSavedRevision(context); owner.LastContext = context; owner.Started.TrySetResult();
            if (owner.HoldCallback) await owner.CallbackGate.Task.WaitAsync(cancellationToken);
            if (owner.ThrowAfterCallbackGate) throw new IOException("fixture_callback_unverified");
            if (owner.BeforeSubmission is { } before) await before();
            if (owner.SubmitNotification)
            {
                var submission = new NexusPipeline.Extensibility.Protocol.NotificationSubmission {
                    Format = "nexus.notification.submission", Version = 1, Invocation = context.Invocation,
                    OperationId = context.Invocation.NotificationOperationId, NotificationTaskIds = context.Invocation.NotificationTaskIds,
                    Title = "scheduled actual source", Body = "saved revision and actual callback" };
                try { _ = await owner.Port!.SubmitAsync(submission, cancellationToken); }
                catch (SqliteException error) when (owner.RetrySubmissionAfterSqlFailure)
                {
                    owner.SubmissionFailure.TrySetResult(error);
                    await owner.RetrySubmission.Task.WaitAsync(cancellationToken);
                    _ = await owner.Port!.SubmitAsync(submission, cancellationToken);
                    _ = await owner.Port!.SubmitAsync(submission, cancellationToken);
                }
                catch (Exception error) { owner.SubmissionFailure.TrySetResult(error); throw; }
            }
            return new() { Format = "nexus.schedule.work-result", Version = 1, Invocation = context.Invocation,
                BusinessOutcome = "notApplicable", ReasonCode = "fixture_planned_callback_completed" };
        }
    }
    private sealed class Extension(Func<CancellationToken, ValueTask> activate) : IExtension
    {
        public ValueTask ActivateAsync(IExtensionScope scope, CancellationToken cancellationToken) => activate(cancellationToken);
    }
    internal sealed class MutableClock : TimeProvider
    {
        private long _ticks;
        internal DateTimeOffset Initial { get; } = DateTimeOffset.Parse("2032-06-07T12:10:00Z", global::System.Globalization.CultureInfo.InvariantCulture);
        internal MutableClock() { _ticks = Initial.Ticks; }
        internal Action? BeforeUtcRead { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            BeforeUtcRead?.Invoke(); return new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        }
        internal void Move(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
    }
}
