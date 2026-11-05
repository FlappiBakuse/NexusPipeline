using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Scheduling;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Scheduling;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal class InstalledManualWork(IManualWorkHandler handler, InstalledExtensionConsumption consumption,
    PersistentScheduleService scheduling, ExtensionScope scope) : IManualWorkHandler
{
    public string? IdentityVersion => handler.IdentityVersion;
    public Extensibility.Secrets.SecretDraft ProjectIdentity(string field, Extensibility.Secrets.SecretDraft material) =>
        consumption.ProjectIdentity(() => handler.ProjectIdentity(field, material));
    public bool CanRotateIdentity(string field, Extensibility.Secrets.SecretDraft before, Extensibility.Secrets.SecretDraft after) =>
        consumption.ProjectIdentity(() => handler.CanRotateIdentity(field, before, after));
    internal static IManualWorkHandler Create(IManualWorkHandler handler, InstalledExtensionConsumption consumption,
        PersistentScheduleService scheduling, ExtensionScope scope) => handler is IScheduledWorkPlanningHandler planning
        ? new Combined(handler, consumption, scheduling, scope, new InstalledSchedulePort.Planning(consumption, planning, scheduling, scope))
        : new InstalledManualWork(handler, consumption, scheduling, scope);

    public async ValueTask<ManualWorkTask?> ReadTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        using var use = await consumption.BeginAsync(cancellationToken).ConfigureAwait(false);
        var task = await handler.ReadTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        use.RequireCurrent(); return task;
    }

    public async ValueTask<ScheduledWorkResult> ExecuteAsync(ScheduledInvocation invocation, CancellationToken cancellationToken)
    {
        using var use = consumption.Begin();
        var result = await handler.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
        use.RequireCurrent(); return result;
    }

    public async ValueTask<ScheduledWorkResult> ExecuteManualAsync(ManualWorkInvocation invocation, CancellationToken cancellationToken)
    {
        var accepted = scheduling.PrepareManualCallback(scope, invocation);
        var root = accepted.AcquireRoot();
        try
        {
            return await consumption.InvokeAcceptedAsync(root, accepted.RequireCurrent,
                () => handler.ExecuteManualAsync(accepted.Invocation, cancellationToken), work: accepted,
                invocationCancellation: cancellationToken).ConfigureAwait(false);
        }
        finally { root.Dispose(); await root.WhenReleased.ConfigureAwait(false); }
    }

    private sealed class Combined : InstalledManualWork, IScheduledWorkPlanningHandler
    {
        private readonly IScheduledWorkPlanningHandler _planning;
        internal Combined(IManualWorkHandler handler, InstalledExtensionConsumption consumption, PersistentScheduleService scheduling,
            ExtensionScope scope, IScheduledWorkPlanningHandler planning) : base(handler, consumption, scheduling, scope) { _planning = planning; }
        public ValueTask<ScheduledWorkPlans> ReadPlansAsync(CancellationToken cancellationToken) => _planning.ReadPlansAsync(cancellationToken);
        public ValueTask<ScheduledWorkResult> ExecutePlannedAsync(ScheduledPlannedInvocation invocation, CancellationToken cancellationToken) =>
            _planning.ExecutePlannedAsync(invocation, cancellationToken);
    }
}
