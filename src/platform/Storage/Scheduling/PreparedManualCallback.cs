using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;
using CoreSubmission = NexusPipeline.Core.Notifications.NotificationSubmission;

namespace NexusPipeline.Platform.Storage.Scheduling;

[SupportedOSPlatform("windows")]
internal sealed class PreparedManualCallback(ManualWorkRuntime owner, ResourceLeaseManager resources, ExtensionScope scope,
    AcceptedManualWork actual, ManualInvocation context, ManualWorkInvocation wire) : IInstalledWork
{
    internal ManualWorkInvocation Invocation => wire;
    public AcceptedExecutionRun Activity => actual.Activity;
    public Guid OperationId => actual.Source.Document.OperationId;
    public FrozenScheduledInvocation FrozenInvocation => context.Invocation;
    public Guid TaskId => actual.Source.Document.Task.TaskId;
    public string ProviderId => actual.Entry.ProviderId;
    public long TaskRevision => context.TaskRevision;
    public DateOnly LocalDate => actual.Source.Document.LocalDate;
    public string TimeZoneId => actual.Source.Document.LocalTimeZoneId;
    public string RequireIdentity() { RequireCurrent(); return actual.Entry.RequireIdentity(); }
    public Core.Secrets.SecretValue ProjectIdentity(string field, Core.Secrets.SecretValue material)
    { RequireCurrent(); return actual.Entry.ProjectIdentity(field, material); }
    public bool CanRotateIdentity(string field, Core.Secrets.SecretValue before, Core.Secrets.SecretValue after)
    { RequireCurrent(); return actual.Entry.CanRotateIdentity(field, before, after); }
    public void RequireCurrent()
    {
        if (!ReferenceEquals(actual, owner.RequireActual(context, scope.Activation.ExtensionId, actual.Entry.ProviderId, scope)))
            throw new UnauthorizedAccessException("manual_work_invocation_inactive");
    }

    internal WorkNotificationOrigin BindNotification(RuntimeInstance instance, ContractReader contracts,
        InstanceLease lease, ExtensionScope submissionScope, CoreSubmission submission)
    {
        lease.VerifyHeld(instance); RequireCurrent();
        if (!submissionScope.IsPublished || submissionScope.ActivationId != actual.Entry.ActivationId ||
            submission.ExtensionId != submissionScope.Activation.ExtensionId || !FrozenInvocation.Matches(submission.Invocation) ||
            submission.OperationId != FrozenInvocation.NotificationOperationId ||
            submission.NotificationTaskIds.Any(id => !FrozenInvocation.NotificationTaskIds.Contains(id)))
            throw new UnauthorizedAccessException("manual_notification_invocation_unbound");
        return new(actual.Source.Reference, FrozenInvocation, actual.Activity, actual.Budget, resources,
            AcceptedManualWork.Claims(actual.Entry.ExtensionId), "manual_notification_admission_blocked", (connection, transaction) =>
            {
                RequireCurrent();
                var state = ManualWorkRows.Read(instance, contracts, connection, transaction, actual.Source.Reference);
                if (state.Begin is null || state.Result is not null || state.Source.Document.Creator != JournalWriterStore.CurrentWriter ||
                    state.Source.Document.OwnerGeneration != lease.StartupGeneration) throw new IOException("notification_manual_source_unbound");
            });
    }

    internal ResourceOperationLease AcquireRoot()
    {
        RequireCurrent();
        var root = resources.TryContinueRun(actual.Activity, actual.Source.Document.OperationId,
            [new("extension:" + scope.Activation.ExtensionId, ResourceClaimMode.Reference)]).Lease ??
            throw new IOException("manual_work_admission_blocked");
        try { RequireCurrent(); return root; }
        catch { root.Dispose(); throw; }
    }
}
