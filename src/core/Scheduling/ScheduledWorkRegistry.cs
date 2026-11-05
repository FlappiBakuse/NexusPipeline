using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Secrets;

namespace NexusPipeline.Core.Scheduling;

public sealed record ScheduledBusinessResult(BusinessOutcome Business, string ReasonCode);

public interface IScheduledWorkSource
{
    ValueTask<ScheduledBusinessResult> ExecuteAsync(FrozenScheduledInvocation invocation, CancellationToken cancellationToken);
}

public interface IScheduledInvocationAuthorization
{
    void AssertAllowed(FrozenScheduledInvocation invocation, string extensionId, string providerId);
}

public sealed class ScheduledWorkRegistry(IScheduledInvocationAuthorization authorization)
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Extension, string Provider), Registration> _registrations = [];

    public IDisposable Register(ExtensionActivation activation, string providerId, IScheduledWorkSource source)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(source);
        ExtensionIdentifiers.Require(providerId);
        lock (_gate)
        {
            if (!activation.IsAccepting) throw new InvalidOperationException("extension_activation_revoked");
            if (_registrations.Count >= 256) throw new InvalidOperationException("scheduled_provider_limit");
            var registration = new Registration(this, activation, providerId, source);
            if (!_registrations.TryAdd((activation.ExtensionId, providerId), registration))
            {
                registration.Cancellation.Dispose();
                throw new InvalidOperationException("scheduled_provider_already_registered");
            }
            return registration;
        }
    }

    public IReadOnlyList<string> PublishedProviderIds(string extensionId)
    {
        lock (_gate) return Array.AsReadOnly(_registrations.Values.Where(item => item.Activation.ExtensionId == extensionId && item.Activation.IsPublished)
            .Select(item => item.ProviderId).Order(StringComparer.Ordinal).ToArray());
    }

    public ValueTask<ScheduledBusinessResult> DispatchAsync(string extensionId, string providerId, FrozenScheduledInvocation invocation,
        CancellationToken cancellationToken) => DispatchCoreAsync(extensionId, providerId, invocation, cancellationToken, null, null, null);

    private async ValueTask<ScheduledBusinessResult> DispatchCoreAsync(string extensionId, string providerId, FrozenScheduledInvocation invocation,
        CancellationToken cancellationToken, PublishedWorkEntry? expected, PlannedScheduledInvocation? planned, ManualInvocation? manual)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        Registration registration;
        lock (_gate)
        {
            if (!_registrations.TryGetValue((extensionId, providerId), out registration!) || !registration.Activation.IsPublished ||
                expected is not null && (!ReferenceEquals(registration, expected.RegistrationIdentity) || registration.Activation.ActivationId != expected.ActivationId))
                throw new InvalidOperationException("scheduled_provider_unavailable");
        }
        using var work = registration.Tasks.EnterExclusive(planned?.OwnerTaskId ?? invocation.SubjectId, invocation.RunId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, registration.Token, work.Cancellation);
        linked.Token.ThrowIfCancellationRequested();
        var plannedAuthorization = planned is null ? null : authorization as IPlannedInvocationAuthorization ??
            throw new UnauthorizedAccessException("scheduled_planned_authorization_required");
        var manualAuthorization = manual is null ? null : authorization as IManualInvocationAuthorization ??
            throw new UnauthorizedAccessException("manual_work_authorization_required");
        if (manual is not null && (planned is not null || !manual.Invocation.Matches(invocation) || registration.Source is not IManualWorkSource))
            throw new UnauthorizedAccessException("manual_work_source_required");
        if (planned is not null && (!planned.Invocation.Matches(invocation) || registration.Source is not IWorkPlanSource))
            throw new UnauthorizedAccessException("scheduled_planned_source_required");
        if (manual is not null) manualAuthorization!.AssertAllowed(manual, extensionId, providerId);
        else if (planned is null) authorization.AssertAllowed(invocation, extensionId, providerId);
        else plannedAuthorization!.AssertAllowed(planned, extensionId, providerId);
        var result = (manual is not null ? await ((IManualWorkSource)registration.Source).ExecuteManualAsync(manual, linked.Token).ConfigureAwait(false) :
            planned is null ? await registration.Source.ExecuteAsync(invocation, linked.Token).ConfigureAwait(false) :
            await ((IWorkPlanSource)registration.Source).ExecutePlannedAsync(planned, linked.Token).ConfigureAwait(false))
            ?? throw new InvalidOperationException("scheduled_work_result_missing");
        linked.Token.ThrowIfCancellationRequested();
        if (manual is not null) manualAuthorization!.AssertAllowed(manual, extensionId, providerId);
        else if (planned is null) authorization.AssertAllowed(invocation, extensionId, providerId);
        else plannedAuthorization!.AssertAllowed(planned, extensionId, providerId);
        if (!Enum.IsDefined(result.Business) || string.IsNullOrEmpty(result.ReasonCode) || result.ReasonCode.Length > 80 ||
            result.ReasonCode[0] is not (>= 'a' and <= 'z') || result.ReasonCode.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')))
            throw new InvalidOperationException("scheduled_work_result_invalid");
        lock (_gate)
        {
            if (!registration.Activation.IsPublished || !_registrations.TryGetValue((extensionId, providerId), out var current) || !ReferenceEquals(registration, current))
                throw new InvalidOperationException("scheduled_provider_revoked");
        }
        return result;
    }

    public IReadOnlyList<PublishedWorkEntry> PublishedPlanningEntries()
    {
        lock (_gate)
            return Array.AsReadOnly(_registrations.Values.Where(registration =>
                registration.Activation.IsPublished && registration.Source is IWorkPlanSource)
                .OrderBy(registration => registration.Activation.ExtensionId, StringComparer.Ordinal)
                .ThenBy(registration => registration.ProviderId, StringComparer.Ordinal)
                .Select(registration => new PublishedWorkEntry(this, registration, registration.Activation.ExtensionId,
                    registration.ProviderId, registration.Activation.ActivationId)).ToArray());
    }

    public PublishedWorkEntry RequireManualEntry(string extensionId, string providerId)
    {
        lock (_gate)
        {
            if (!_registrations.TryGetValue((extensionId, providerId), out var registration) ||
                !registration.Activation.IsPublished || registration.Source is not IManualWorkSource)
                throw new InvalidOperationException("manual_work_provider_unavailable");
            return new(this, registration, extensionId, providerId, registration.Activation.ActivationId);
        }
    }

    internal async ValueTask<ObservedManualTask?> ReadManualAsync(PublishedWorkEntry entry, Guid taskId, CancellationToken cancellation)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("manual_work_task_invalid");
        var registration = (Registration)RequireCurrent(entry);
        if (registration.Source is not IManualWorkSource source) throw new InvalidOperationException("manual_work_provider_unavailable");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, registration.Token);
        linked.Token.ThrowIfCancellationRequested();
        var task = await source.ReadTaskAsync(taskId, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested(); _ = RequireCurrent(entry);
        if (task is null) return null;
        ManualWorkPolicy.Validate(task);
        if (task.TaskId != taskId) throw new InvalidOperationException("manual_work_task_unbound");
        return new(entry, task);
    }

    internal async ValueTask<IDisposable> QuiesceAsync(PublishedWorkEntry entry, IReadOnlyList<Guid> taskIds, CancellationToken cancellation)
    {
        var registration = (Registration)RequireCurrent(entry);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, registration.Token);
        var held = await registration.Tasks.QuiesceAsync(taskIds, linked.Token).ConfigureAwait(false);
        try { _ = RequireCurrent(entry); return held; }
        catch { held.Dispose(); throw; }
    }

    internal WorkTaskUse EnterTask(PublishedWorkEntry entry, Guid taskId)
    {
        var registration = (Registration)RequireCurrent(entry);
        var use = registration.Tasks.Enter(taskId);
        try { _ = RequireCurrent(entry); return use; }
        catch { use.Dispose(); throw; }
    }

    internal bool SupportsManual(PublishedWorkEntry entry) => ((Registration)RequireCurrent(entry)).Source is IManualWorkSource;

    internal void RequireExecution(PublishedWorkEntry entry, Guid taskId, Guid runId) =>
        ((Registration)RequireCurrent(entry)).Tasks.RequireExclusive(taskId, runId);

    internal string RequireIdentity(PublishedWorkEntry entry)
    {
        var source = ((Registration)RequireCurrent(entry)).Source as IManualWorkSource;
        var version = source?.IdentityVersion;
        if (version is not { Length: >= 1 and <= 32 } || version.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            throw new UnauthorizedAccessException("work_identity_unavailable");
        return version;
    }

    internal SecretValue ProjectIdentity(PublishedWorkEntry entry, string field, SecretValue material)
    {
        _ = RequireIdentity(entry);
        var source = (IManualWorkSource)((Registration)RequireCurrent(entry)).Source;
        var result = source.ProjectIdentity(field, material) ?? throw new InvalidOperationException("work_identity_missing");
        try { _ = RequireCurrent(entry); return result; }
        catch { result.Dispose(); throw; }
    }

    internal bool CanRotateIdentity(PublishedWorkEntry entry, string field, SecretValue before, SecretValue after)
    {
        _ = RequireIdentity(entry);
        var source = (IManualWorkSource)((Registration)RequireCurrent(entry)).Source;
        var result = source.CanRotateIdentity(field, before, after); _ = RequireCurrent(entry); return result;
    }

    internal async ValueTask<ObservedWorkPlans> ReadPlansAsync(PublishedWorkEntry entry, CancellationToken cancellation)
    {
        var registration = (Registration)RequireCurrent(entry);
        if (registration.Source is not IWorkPlanSource source) throw new InvalidOperationException("scheduled_plans_unavailable");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, registration.Token);
        linked.Token.ThrowIfCancellationRequested();
        var plans = await source.ReadPlansAsync(linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        _ = RequireCurrent(entry); WorkPlanPolicy.Validate(plans);
        return new(entry, plans);
    }

    internal object RequireCurrent(PublishedWorkEntry entry)
    {
        lock (_gate)
        {
            if (!_registrations.TryGetValue((entry.ExtensionId, entry.ProviderId), out var registration) ||
                !ReferenceEquals(registration, entry.RegistrationIdentity) || !registration.Activation.IsPublished ||
                registration.Activation.ActivationId != entry.ActivationId)
                throw new InvalidOperationException("scheduled_provider_revoked");
            return registration;
        }
    }

    internal async ValueTask<ScheduledBusinessResult> DispatchEntryAsync(PublishedWorkEntry entry,
        FrozenScheduledInvocation invocation, CancellationToken cancellation)
    {
        _ = RequireCurrent(entry);
        return await DispatchCoreAsync(entry.ExtensionId, entry.ProviderId, invocation, cancellation, entry, null, null).ConfigureAwait(false);
    }

    internal ValueTask<ScheduledBusinessResult> DispatchPlannedAsync(PublishedWorkEntry entry,
        PlannedScheduledInvocation invocation, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(invocation); _ = RequireCurrent(entry);
        return DispatchCoreAsync(entry.ExtensionId, entry.ProviderId, invocation.Invocation, cancellation, entry, invocation, null);
    }

    internal ValueTask<ScheduledBusinessResult> DispatchManualAsync(PublishedWorkEntry entry, ManualInvocation invocation, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(invocation); _ = RequireCurrent(entry);
        return DispatchCoreAsync(entry.ExtensionId, entry.ProviderId, invocation.Invocation, cancellation, entry, null, invocation);
    }

    private void Remove(Registration registration)
    {
        lock (_gate)
        {
            var key = (registration.Activation.ExtensionId, registration.ProviderId);
            if (!_registrations.TryGetValue(key, out var current) || !ReferenceEquals(current, registration)) return;
            _registrations.Remove(key);
        }
        try { registration.Cancellation.Cancel(); }
        finally { registration.Cancellation.Dispose(); }
    }

    private sealed class Registration : IDisposable
    {
        internal ExtensionActivation Activation { get; }
        internal string ProviderId { get; }
        internal IScheduledWorkSource Source { get; }
        internal WorkTaskGate Tasks { get; } = new();
        internal CancellationTokenSource Cancellation { get; } = new();
        internal CancellationToken Token { get; }
        private readonly ScheduledWorkRegistry _owner;

        internal Registration(ScheduledWorkRegistry owner, ExtensionActivation activation, string providerId, IScheduledWorkSource source)
        {
            _owner = owner; Activation = activation; ProviderId = providerId; Source = source; Token = Cancellation.Token;
        }

        public void Dispose() => _owner.Remove(this);
    }
}
