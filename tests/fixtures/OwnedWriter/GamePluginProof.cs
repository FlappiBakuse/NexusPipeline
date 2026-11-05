using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Secrets;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;

internal sealed class GamePluginProof
{
    private readonly PluginSliceAssembly _plugin;
    private readonly FrozenScheduledInvocation _invocation;
    private readonly ContractReader _reader = new();
    private readonly CheckInAuthorization _authorization;
    private readonly CheckInSecrets _store;
    private readonly CheckInQueue _queue = new();
    private readonly ScheduledWorkRegistry _registry;
    private readonly ExtensionActivator _activator;
    private ApplyCredential? _apply;
    private GetCredential? _get;

    internal GamePluginProof(PluginSliceAssembly plugin, FrozenScheduledInvocation invocation)
    {
        _plugin = plugin; _invocation = invocation; _authorization = new(invocation);
        _store = new(invocation.InstanceId); _registry = new(_authorization); _activator = new(_reader);
    }

    internal async Task<object> RunAsync(char[] canary)
    {
        var pendingHidden = false;
        await using var activation = await _activator.ActivateManagedAsync(_plugin.ManifestBytes, "game-checkin", (descriptor, scope) =>
            new ActivationProbe(Factory(descriptor, scope), () =>
            {
                pendingHidden = _registry.PublishedProviderIds("game-checkin").Count == 0;
                Require(pendingHidden, "partial_checkin_registration_was_visible");
            }), default);
        var credentials = await VerifyCredentialsAsync(canary);
        var lifecycle = await VerifyLifecycleAsync(activation, canary);
        return new
        {
            status = "PASS", scope = "Actual plugin/public SDK/Windows CurrentUser protection in an owned fixture worker; memory fixture secret store and notification queue; no real check-in, persistent scheduler or recipient delivery",
            processId = Environment.ProcessId, assemblySha256 = _plugin.AssemblySha256, references = _plugin.References,
            contractFingerprint = NexusPipeline.Extensibility.SdkIdentity.ContractFingerprint,
            context = new { instanceId = _invocation.InstanceId, runId = _invocation.RunId, attemptId = _invocation.AttemptId }, occurrenceId = _invocation.OccurrenceId,
            pendingHidden, keepPreservedCiphertext = credentials.Kept == credentials.Saved && credentials.CiphertextDigest is not null,
            explicitClearRevoked = !credentials.Cleared.IsConfigured, unreadableRemainedConfigured = credentials.Unavailable.IsConfigured && !credentials.Unavailable.IsReadable,
            lateRejected = lifecycle.LateRejected, failedActivationRolledBack = lifecycle.RolledBack,
            reactivationReturnedBusinessNotApplicable = lifecycle.Resumed, secretReadCalls = _store.ReadCalls,
            notificationSubmitCalls = _queue.Calls, acceptedNotificationOperations = _queue.AcceptedCount,
            windowsProtectionScheme = NexusPipeline.Platform.Security.UserSecretProtector.Scheme, platformHttp = "NOT_RUN", browserSurface = "NOT_RUN"
        };
    }

    private async Task<CredentialEvidence> VerifyCredentialsAsync(char[] canary)
    {
        var initial = await _get!(_invocation.SubjectId, default);
        Require(!initial.IsConfigured && initial.Revision == 0 && _queue.Calls == 0, "checkin_started_unrequested_work");
        var draft = new SecretDraft(canary);
        var saved = await _apply!(Guid.NewGuid(), _invocation.SubjectId, 0, SecretChange.Set(draft), default);
        Require(saved.IsConfigured && saved.IsReadable && saved.Revision == 1, "checkin_set_did_not_commit");
        Require(Disposed(draft), "checkin_set_retained_plaintext_draft");
        var digest = _store.CiphertextDigest;
        var kept = await _apply(Guid.NewGuid(), _invocation.SubjectId, 1, SecretChange.Keep(), default);
        Require(kept == saved && digest == _store.CiphertextDigest, "checkin_keep_changed_ciphertext_or_reference");
        var first = await DispatchAsync();
        var repeated = await DispatchAsync();
        Require(first.Business == BusinessOutcome.NotApplicable && repeated == first && _queue.Calls == 2 && _queue.AcceptedCount == 1,
            "checkin_fixture_notification_identity_or_business_fact_differs");
        _store.CorruptCiphertext();
        var unavailable = await _get(_invocation.SubjectId, default);
        Require(unavailable.IsConfigured && !unavailable.IsReadable && unavailable.SecretId == saved.SecretId, "unreadable_checkin_secret_became_empty");
        var blocked = await DispatchAsync();
        Require(blocked.Business == BusinessOutcome.Unverified && _queue.Calls == 2, "unreadable_checkin_secret_did_not_block_work");
        var cleared = await _apply(Guid.NewGuid(), _invocation.SubjectId, 1, SecretChange.Clear(), default);
        Require(!cleared.IsConfigured && !cleared.IsReadable && cleared.SecretId is null && cleared.Revision == 2, "explicit_checkin_clear_failed");
        var replaced = await _apply(Guid.NewGuid(), _invocation.SubjectId, 2, SecretChange.Set(new(canary)), default);
        Require(replaced.SecretId != saved.SecretId && replaced.Revision == 3, "checkin_replacement_did_not_get_a_new_reference");
        return new(saved, kept, unavailable, cleared, digest);
    }

    private async Task<LifecycleEvidence> VerifyLifecycleAsync(ExtensionScope activation, char[] canary)
    {
        _store.DelayRead = true;
        var late = DispatchAsync().AsTask();
        await _store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await activation.DisposeAsync();
        _store.Release.TrySetResult();
        var rejected = await Cancelled(late);
        Require(rejected, "late_checkin_callback_outlived_activation");
        Require(_store.LastReadValue is not null && Disposed(_store.LastReadValue), "late_checkin_plaintext_was_not_released");
        Require(_queue.Calls == 2 && _registry.PublishedProviderIds("game-checkin").Count == 0, "late_checkin_work_reached_notification_queue");
        var deniedDraft = new SecretDraft(canary);
        var denied = _apply!(Guid.NewGuid(), _invocation.SubjectId, 3, SecretChange.Set(deniedDraft), default).AsTask();
        Require(await Cancelled(denied) && Disposed(deniedDraft), "retained_checkin_service_outlived_activation");
        var rolledBack = false;
        try
        {
            await _activator.ActivateManagedAsync(_plugin.ManifestBytes, "game-checkin", (descriptor, scope) =>
                new ActivationProbe(Factory(descriptor, scope), () => throw new IOException("checkin_fixture_activation_failure")), default);
        }
        catch (IOException error) when (error.Message == "checkin_fixture_activation_failure") { rolledBack = true; }
        Require(rolledBack && _registry.PublishedProviderIds("game-checkin").Count == 0, "failed_checkin_activation_left_a_provider");
        _store.DelayRead = false;
        await using var reactivated = await _activator.ActivateManagedAsync(_plugin.ManifestBytes, "game-checkin", Factory, default);
        var resumed = await DispatchAsync();
        Require(resumed.Business == BusinessOutcome.NotApplicable && _queue.AcceptedCount == 1, "checkin_reactivation_did_not_use_host_acceptance");
        await reactivated.DisposeAsync();
        return new(rejected, rolledBack, resumed.Business == BusinessOutcome.NotApplicable);
    }

    private IExtension Factory(ExtensionManifest descriptor, ExtensionScope scope)
    {
        var schedules = new ScheduleRegistrationPort(descriptor, scope, _registry, _reader);
        var secrets = new ExtensionSecretPort(descriptor, scope, _authorization, _store, _reader);
        var notifications = new NotificationSubmissionPort(descriptor, scope, _authorization, _queue, _reader);
        var extension = (IExtension)(Activator.CreateInstance(_plugin.ExtensionType, SliceActivationContext.Create(schedules, secrets, notifications)) ?? throw new IOException("checkin_factory_missing"));
        var credentials = _plugin.ExtensionType.GetProperty("Credentials")!.GetValue(extension)!;
        _apply = credentials.GetType().GetMethod("ApplyAsync")!.CreateDelegate<ApplyCredential>(credentials);
        _get = credentials.GetType().GetMethod("GetStateAsync")!.CreateDelegate<GetCredential>(credentials);
        return extension;
    }

    private ValueTask<ScheduledBusinessResult> DispatchAsync() => _registry.DispatchAsync("game-checkin", "credential-check", _invocation, default);
    private static bool Disposed(SecretDraft value) { try { value.Use(_ => { }); return false; } catch (ObjectDisposedException) { return true; } }
    private static bool Disposed(NexusPipeline.Core.Secrets.SecretValue value) { try { value.Use(_ => { }); return false; } catch (ObjectDisposedException) { return true; } }
    private static async Task<bool> Cancelled(Task value) { try { await value; return false; } catch (OperationCanceledException) { return true; } }
    private static void Require(bool condition, string reason) { if (!condition) throw new IOException(reason); }
    private delegate ValueTask<SecretFieldState> ApplyCredential(Guid operationId, Guid taskId, long revision, SecretChange change, CancellationToken cancellationToken);
    private delegate ValueTask<SecretFieldState> GetCredential(Guid taskId, CancellationToken cancellationToken);
    private sealed record CredentialEvidence(SecretFieldState Saved, SecretFieldState Kept, SecretFieldState Unavailable, SecretFieldState Cleared, string? CiphertextDigest);
    private sealed record LifecycleEvidence(bool LateRejected, bool RolledBack, bool Resumed);

    private sealed class ActivationProbe(IExtension inner, Action verify) : IExtension
    {
        public async ValueTask ActivateAsync(IExtensionScope scope, CancellationToken cancellationToken)
        {
            await inner.ActivateAsync(scope, cancellationToken);
            verify();
        }
    }
}
