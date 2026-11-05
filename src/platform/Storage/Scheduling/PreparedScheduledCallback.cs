using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Storage.Scheduling;

[SupportedOSPlatform("windows")]
internal sealed class PreparedScheduledCallback(ScheduledProviderDispatch owner, ExtensionScope scope,
    AcceptedProviderInvocation actual, PlannedScheduledInvocation context, ScheduledPlannedInvocation wire) : IInstalledWork
{
    internal ScheduledPlannedInvocation Invocation => wire;
    public AcceptedExecutionRun Activity => actual.Activity;
    public Guid OperationId => actual.Source.Document.OperationId;
    public FrozenScheduledInvocation FrozenInvocation => context.Invocation;
    public Guid TaskId => actual.Source.Document.ManualTask?.TaskId ?? context.Invocation.SubjectId;
    public string ProviderId => actual.Entry.ProviderId;
    public long TaskRevision => context.TaskRevision;
    public DateOnly LocalDate => actual.Source.Document.HostLocalDate;
    public string TimeZoneId => actual.Source.Document.HostTimeZoneId;
    public string RequireIdentity() { RequireCurrent(); return actual.Entry.RequireIdentity(); }
    public Core.Secrets.SecretValue ProjectIdentity(string field, Core.Secrets.SecretValue material)
    { RequireCurrent(); return actual.Entry.ProjectIdentity(field, material); }
    public bool CanRotateIdentity(string field, Core.Secrets.SecretValue before, Core.Secrets.SecretValue after)
    { RequireCurrent(); return actual.Entry.CanRotateIdentity(field, before, after); }
    public void RequireCurrent()
    {
        var current = owner.RequireActual(context.Invocation, scope.Activation.ExtensionId, actualScope: scope);
        if (!ReferenceEquals(current, actual)) throw new UnauthorizedAccessException("scheduled_invocation_not_active");
        owner.AssertAllowed(context, scope.Activation.ExtensionId, actual.Entry.ProviderId);
    }

    internal ResourceOperationLease AcquireRoot()
    {
        RequireCurrent();
        var root = owner.AcquireCallbackRoot(actual);
        try { RequireCurrent(); return root; }
        catch { root.Dispose(); throw; }
    }
}
