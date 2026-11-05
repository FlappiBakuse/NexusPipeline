using System.Collections.Immutable;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Scheduling;
using NexusPipeline.Platform.Serialization;
using WireTask = NexusPipeline.Extensibility.Protocol.ManualWorkTask;

namespace NexusPipeline.Platform.Extensions;

internal static class ManualWorkSources
{
    internal static IScheduledWorkSource Create(IManualWorkHandler handler, ContractReader reader) =>
        handler is IScheduledWorkPlanningHandler planning ? new Combined(handler, planning, reader) : new Manual(handler, reader);

    private sealed class Manual : ScheduleRegistrationPort.Source, IManualWorkSource
    {
        private readonly IManualWorkHandler _handler;
        private readonly ContractReader _reader;
        private readonly WorkIdentityProjector _identity;
        internal Manual(IManualWorkHandler handler, ContractReader reader) : base(handler, reader)
        { _handler = handler; _reader = reader; _identity = new(handler); }
        public string? IdentityVersion => _identity.Version;
        public Core.Secrets.SecretValue ProjectIdentity(string field, Core.Secrets.SecretValue material) => _identity.Project(field, material);
        public bool CanRotateIdentity(string field, Core.Secrets.SecretValue before, Core.Secrets.SecretValue after) => _identity.CanRotate(field, before, after);
        public ValueTask<Core.Scheduling.ManualWorkTask?> ReadTaskAsync(Guid taskId, CancellationToken cancellation) => ReadAsync(_handler, _reader, taskId, cancellation);
        public ValueTask<ScheduledBusinessResult> ExecuteManualAsync(ManualInvocation invocation, CancellationToken cancellation) =>
            ManualWorkSources.ExecuteAsync(_handler, _reader, invocation, cancellation);
    }

    private sealed class Combined : ScheduleRegistrationPort.PlanningSource, IManualWorkSource
    {
        private readonly IManualWorkHandler _handler;
        private readonly ContractReader _reader;
        private readonly WorkIdentityProjector _identity;
        internal Combined(IManualWorkHandler handler, IScheduledWorkPlanningHandler planning, ContractReader reader) : base(planning, reader)
        { _handler = handler; _reader = reader; _identity = new(handler); }
        public string? IdentityVersion => _identity.Version;
        public Core.Secrets.SecretValue ProjectIdentity(string field, Core.Secrets.SecretValue material) => _identity.Project(field, material);
        public bool CanRotateIdentity(string field, Core.Secrets.SecretValue before, Core.Secrets.SecretValue after) => _identity.CanRotate(field, before, after);
        public ValueTask<Core.Scheduling.ManualWorkTask?> ReadTaskAsync(Guid taskId, CancellationToken cancellation) => ReadAsync(_handler, _reader, taskId, cancellation);
        public ValueTask<ScheduledBusinessResult> ExecuteManualAsync(ManualInvocation invocation, CancellationToken cancellation) =>
            ManualWorkSources.ExecuteAsync(_handler, _reader, invocation, cancellation);
    }

    private static async ValueTask<Core.Scheduling.ManualWorkTask?> ReadAsync(IManualWorkHandler handler, ContractReader reader,
        Guid taskId, CancellationToken cancellation)
    {
        var saved = await handler.ReadTaskAsync(taskId, cancellation).ConfigureAwait(false);
        if (saved is null) return null;
        var wire = reader.Read<WireTask>(BoundedJson.Serialize(saved), "manual-work-task.schema.json");
        var result = new Core.Scheduling.ManualWorkTask(Guid.ParseExact(wire.TaskId, "D"), wire.Revision, wire.IsEnabled,
            wire.TimeZoneId, checked((int)wire.TimeoutSeconds), wire.NotificationTaskIds.Select(Guid.Parse).ToImmutableArray(),
            wire.ScheduledTaskIds.Select(Guid.Parse).ToImmutableArray());
        ManualWorkPolicy.Validate(result);
        if (result.TaskId != taskId) throw new InvalidOperationException("manual_work_task_unbound");
        return result;
    }

    private static async ValueTask<ScheduledBusinessResult> ExecuteAsync(IManualWorkHandler handler, ContractReader reader,
        ManualInvocation invocation, CancellationToken cancellation)
    {
        var wire = reader.Read<ManualWorkInvocation>(BoundedJson.Serialize(new ManualWorkInvocation
        {
            Format = "nexus.manual.work-invocation", Version = 1,
            Invocation = ScheduledInvocationWire.ToWire(invocation.Invocation), TaskRevision = invocation.TaskRevision
        }), "manual-work-invocation.schema.json");
        var result = await handler.ExecuteManualAsync(wire, cancellation).ConfigureAwait(false)
            ?? throw new System.Text.Json.JsonException("manual_work_result_missing");
        return ScheduleRegistrationPort.Source.ReadResult(invocation.Invocation, result, reader);
    }
}
