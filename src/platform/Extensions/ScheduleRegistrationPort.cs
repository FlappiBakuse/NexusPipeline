using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Scheduling;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Extensions;

public sealed class ScheduleRegistrationPort : IScheduleRegistrationPort
{
    private readonly ExtensionScope _scope;
    private readonly ScheduledWorkRegistry _registry;
    private readonly ContractReader _reader;
    private readonly bool _declared;

    public ScheduleRegistrationPort(ExtensionManifest manifest, ExtensionScope scope, ScheduledWorkRegistry registry, ContractReader reader)
    {
        var admitted = new ExtensionManifestAdmission(reader).ReadCurrent(JsonSerializer.SerializeToUtf8Bytes(manifest), scope.Activation.ExtensionId);
        _scope = scope; _registry = registry; _reader = reader;
        _declared = admitted.Capabilities.Contains("schedule.register", StringComparer.Ordinal);
    }

    public async ValueTask RegisterAsync(string providerId, IScheduledWorkHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        cancellationToken.ThrowIfCancellationRequested();
        _scope.Cancellation.ThrowIfCancellationRequested();
        if (!_declared) throw new UnauthorizedAccessException("schedule_register_capability_required");
        var registration = _registry.Register(_scope.Activation, providerId, CreateSource(handler, _reader));
        await _scope.OwnAsync(new Lifetime(registration)).ConfigureAwait(false);
        _scope.Cancellation.ThrowIfCancellationRequested();
    }

    private static IScheduledWorkSource CreateSource(IScheduledWorkHandler handler, ContractReader reader) => handler switch
    {
        IManualWorkHandler manual => ManualWorkSources.Create(manual, reader),
        IScheduledWorkPlanningHandler planning => new PlanningSource(planning, reader),
        _ => new Source(handler, reader)
    };

    internal class PlanningSource(IScheduledWorkPlanningHandler handler, ContractReader reader) : IWorkPlanSource
    {
        private readonly Source _execution = new(handler, reader);
        public ValueTask<ScheduledBusinessResult> ExecuteAsync(FrozenScheduledInvocation invocation, CancellationToken cancellationToken) =>
            _execution.ExecuteAsync(invocation, cancellationToken);

        public async ValueTask<ScheduledBusinessResult> ExecutePlannedAsync(PlannedScheduledInvocation invocation, CancellationToken cancellationToken)
        {
            var context = new ScheduledPlannedInvocation { Format = "nexus.schedule.planned-invocation", Version = 1,
                Invocation = ScheduledInvocationWire.ToWire(invocation.Invocation), PlansRevision = invocation.PlansRevision,
                TaskRevision = invocation.TaskRevision, OwnerTaskId = invocation.OwnerTaskId?.ToString("D") };
            var frozen = reader.Read<ScheduledPlannedInvocation>(BoundedJson.Serialize(context), "scheduled-planned-invocation.schema.json");
            var result = await handler.ExecutePlannedAsync(frozen, cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("scheduled_work_result_missing");
            return Source.ReadResult(invocation.Invocation, result, reader);
        }

        public async ValueTask<NexusPipeline.Core.Scheduling.ScheduledWorkPlans> ReadPlansAsync(CancellationToken cancellationToken)
        {
            var saved = await handler.ReadPlansAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("scheduled_plans_missing");
            var wire = reader.Read<NexusPipeline.Extensibility.Protocol.ScheduledWorkPlans>(
                BoundedJson.Serialize(saved), "scheduled-work-plans.schema.json");
            var result = new NexusPipeline.Core.Scheduling.ScheduledWorkPlans(wire.Revision, wire.Tasks.Select(task =>
                new WorkTaskPlan(Guid.ParseExact(task.TaskId, "D"), task.Revision, task.IsEnabled, task.TimeZoneId,
                    task.Weekdays.Select(day => checked((int)day)).ToImmutableArray(), checked((int)task.LocalMinute),
                    checked((int)task.TimeoutSeconds), task.NotificationTaskIds.Select(id => Guid.ParseExact(id, "D")).ToImmutableArray(),
                    task.OwnerTaskId is null ? null : Guid.ParseExact(task.OwnerTaskId, "D"))).ToImmutableArray());
            WorkPlanPolicy.Validate(result); return result;
        }
    }

    private sealed class Lifetime(IDisposable registration) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { registration.Dispose(); return ValueTask.CompletedTask; }
    }

    internal class Source(IScheduledWorkHandler handler, ContractReader reader) : IScheduledWorkSource
    {
        public async ValueTask<ScheduledBusinessResult> ExecuteAsync(FrozenScheduledInvocation invocation, CancellationToken cancellationToken)
        {
            var request = ScheduledInvocationWire.ToWire(invocation);
            var result = await handler.ExecuteAsync(request, cancellationToken).ConfigureAwait(false) ?? throw new JsonException("scheduled_work_result_missing");
            return ReadResult(invocation, result, reader);
        }

        internal static ScheduledBusinessResult ReadResult(FrozenScheduledInvocation invocation, ScheduledWorkResult result, ContractReader reader)
        {
            var request = ScheduledInvocationWire.ToWire(invocation);
            var validated = reader.Read<ScheduledWorkResult>(JsonSerializer.SerializeToUtf8Bytes(result), "scheduled-work-result.schema.json");
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(request), JsonSerializer.SerializeToElement(validated.Invocation)))
                throw new JsonException("scheduled_work_result_invocation_unbound");
            return new(Enum.Parse<BusinessOutcome>(validated.BusinessOutcome, true), validated.ReasonCode);
        }
    }
}
