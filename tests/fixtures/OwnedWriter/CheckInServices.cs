using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Security;

internal sealed class CheckInAuthorization(FrozenScheduledInvocation approved) : IScheduledInvocationAuthorization,
    INotificationSubmissionAuthorization, ISecretAccessPolicy
{
    internal bool Allowed { get; set; } = true;
    public void AssertAllowed(FrozenScheduledInvocation invocation, string extensionId, string providerId)
    {
        if (!Allowed || !approved.Matches(invocation) || extensionId != "game-checkin" || providerId != "credential-check" || invocation.DeadlineUtc <= DateTimeOffset.UtcNow)
            throw new UnauthorizedAccessException("checkin_fixture_invocation_denied");
    }
    public void AssertAllowed(NotificationSubmission submission) => AssertAllowed(submission.Invocation, submission.ExtensionId, "credential-check");
    public void AssertAllowed(SecretFieldKey key, SecretAccessKind access, SecretSubjectScopes declaredScopes)
    {
        if (!Allowed || key.ExtensionId != "game-checkin" || key.SubjectId != approved.SubjectId || key.Field != "cookie" || declaredScopes != SecretSubjectScopes.OwnExtension)
            throw new UnauthorizedAccessException("checkin_fixture_secret_denied");
    }
}

internal sealed class CheckInQueue : INotificationSubmissionQueue
{
    private readonly Dictionary<Guid, (NotificationSubmission Intent, NotificationQueueAcceptance Acceptance)> _entries = [];
    private readonly object _gate = new();
    internal int Calls { get; private set; }
    internal int AcceptedCount { get { lock (_gate) return _entries.Count; } }

    public ValueTask<NotificationQueueAcceptance> AcceptAsync(NotificationSubmission submission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Calls++;
            if (_entries.TryGetValue(submission.OperationId, out var prior))
            {
                if (!prior.Intent.Invocation.Matches(submission.Invocation) || prior.Intent.Title != submission.Title || prior.Intent.Body != submission.Body ||
                    !prior.Intent.NotificationTaskIds.SequenceEqual(submission.NotificationTaskIds)) throw new IOException("checkin_fixture_notification_conflict");
                return ValueTask.FromResult(prior.Acceptance with { IsNew = false });
            }
            var accepted = new NotificationQueueAcceptance(submission.OperationId, Guid.NewGuid(), true);
            _entries.Add(submission.OperationId, (submission, accepted));
            return ValueTask.FromResult(accepted);
        }
    }
}

internal sealed class CheckInSecrets(Guid instanceId) : ISecretFieldStore
{
    private readonly UserSecretProtector _protector = new();
    private readonly object _gate = new();
    private Guid? _secretId;
    private ProtectedSecretPayload? _payload;
    private long _revision;
    internal int ReadCalls { get; private set; }
    internal SecretValue? LastReadValue { get; private set; }
    internal bool DelayRead { get; set; }
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string? CiphertextDigest { get { lock (_gate) return _payload?.Sha256; } }

    public ValueTask<SecretFieldStatus> GetStateAsync(SecretFieldKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult(State());
    }

    public ValueTask<SecretFieldStatus> ApplyAsync(Guid operationId, SecretFieldKey key, long expectedRevision,
        SecretDisposition disposition, SecretValue? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (operationId == Guid.Empty || expectedRevision != _revision) throw new IOException("checkin_fixture_secret_revision_conflict");
            if (disposition == SecretDisposition.Set)
            {
                if (value is null) throw new ArgumentException("checkin_fixture_secret_set_requires_value");
                var id = Guid.NewGuid();
                var encrypted = _protector.Protect(instanceId, id, value);
                _secretId = id; _payload = encrypted; _revision++;
            }
            else if (disposition == SecretDisposition.Clear) { _secretId = null; _payload = null; _revision++; }
            else if (disposition != SecretDisposition.Keep || value is not null) throw new ArgumentException("checkin_fixture_secret_change_invalid");
            return ValueTask.FromResult(State());
        }
    }

    public async ValueTask<SecretValue> ReadAsync(SecretFieldKey key, CancellationToken cancellationToken)
    {
        // The boundary must reject a late result even when this controlled source ignores cancellation.
        if (DelayRead) { Entered.TrySetResult(); await Release.Task.ConfigureAwait(false); }
        lock (_gate)
        {
            ReadCalls++;
            if (_secretId is not Guid id || _payload is null) throw new IOException("checkin_fixture_secret_not_configured");
            return LastReadValue = _protector.Unprotect(instanceId, id, _payload);
        }
    }

    internal void CorruptCiphertext()
    {
        lock (_gate)
        {
            var bytes = (_payload ?? throw new InvalidOperationException()).Ciphertext.ToArray();
            bytes[^1] ^= 1;
            _payload = _payload with { Ciphertext = bytes };
        }
    }

    private SecretFieldStatus State()
    {
        if (_secretId is not Guid id || _payload is null) return new(_revision, null, false, false);
        try { using var plaintext = _protector.Unprotect(instanceId, id, _payload); return new(_revision, id, true, true); }
        catch (IOException) { return new(_revision, id, true, false); }
    }
}
