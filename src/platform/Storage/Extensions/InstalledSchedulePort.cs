using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Extensibility.Scheduling;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledSchedulePort(PreparedExtensionActivation source, InstalledExtensionConsumption consumption,
    IScheduleRegistrationPort inner, PersistentScheduleService scheduling, ExtensionScope scope) : IScheduleRegistrationPort
{
    public async ValueTask RegisterAsync(string providerId, IScheduledWorkHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler); cancellationToken.ThrowIfCancellationRequested();
        source.RequireBeforeCallback();
        IScheduledWorkHandler guarded = handler is IManualWorkHandler manual ? InstalledManualWork.Create(manual, consumption, scheduling, scope) :
            handler is IScheduledWorkPlanningHandler planning ? new Planning(consumption, planning, scheduling, scope) : new Execution(consumption, handler);
        await inner.RegisterAsync(providerId, guarded, cancellationToken).ConfigureAwait(false);
        source.RequireBeforeCallback();
    }

    private sealed class Execution(InstalledExtensionConsumption consumption, IScheduledWorkHandler handler) : IScheduledWorkHandler
    {
        public async ValueTask<ScheduledWorkResult> ExecuteAsync(ScheduledInvocation invocation, CancellationToken cancellationToken)
        {
            using var use = consumption.Begin();
            var result = await handler.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
            use.RequireCurrent(); return result;
        }
    }

    internal sealed class Planning(InstalledExtensionConsumption consumption, IScheduledWorkPlanningHandler handler,
        PersistentScheduleService scheduling, ExtensionScope scope)
        : IScheduledWorkPlanningHandler
    {
        public async ValueTask<ScheduledWorkPlans> ReadPlansAsync(CancellationToken cancellationToken)
        {
            using var use = consumption.Begin();
            var result = await handler.ReadPlansAsync(cancellationToken).ConfigureAwait(false);
            use.RequireCurrent(); return result;
        }

        public async ValueTask<ScheduledWorkResult> ExecuteAsync(ScheduledInvocation invocation, CancellationToken cancellationToken)
        {
            using var use = consumption.Begin();
            var result = await handler.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
            use.RequireCurrent(); return result;
        }

        public async ValueTask<ScheduledWorkResult> ExecutePlannedAsync(ScheduledPlannedInvocation invocation, CancellationToken cancellationToken)
        {
            var accepted = scheduling.PrepareInstalledCallback(scope, invocation);
            var root = accepted.AcquireRoot();
            try
            {
                return await consumption.InvokeAcceptedAsync(root, accepted.RequireCurrent,
                    () => handler.ExecutePlannedAsync(accepted.Invocation, cancellationToken), work: accepted,
                    invocationCancellation: cancellationToken).ConfigureAwait(false);
            }
            finally { root.Dispose(); await root.WhenReleased.ConfigureAwait(false); }
        }
    }
}
