using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
public sealed class ControlTokenService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _dialogSlot = new(1, 1);
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _lease;
    private readonly SecretFieldService _secrets;
    private readonly SecretFieldStore _store;
    private readonly NexusPipeline.Platform.Storage.Operations.OperationQueryService _operations;
    private readonly NativeClientBinding? _native;
    private readonly ILocalConfirmationDialog _dialog;
    private readonly CancellationTokenSource _ending;
    private readonly Dictionary<Guid, Pending> _pending = [];
    private readonly Task _nativeExit;
    private bool _disposed;

    public ControlTokenService(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        NexusPipeline.Core.Maintenance.ResourceLeaseManager resources, SecretFieldService secrets, NativeClientBinding? native = null,
        CancellationToken hostStopping = default)
        : this(instance, database, lease, resources, secrets, native, new LocalConfirmationDialog(), hostStopping) { }

    internal ControlTokenService(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        NexusPipeline.Core.Maintenance.ResourceLeaseManager resources, SecretFieldService secrets, NativeClientBinding? native,
        ILocalConfirmationDialog dialog, CancellationToken hostStopping = default)
    {
        lease.VerifyHeld(instance); secrets.RequireOwner(instance, database, lease, resources);
        _instance = instance; _lease = lease; _secrets = secrets; _store = new(instance, database);
        _operations = new(instance, lease, database);
        _ending = CancellationTokenSource.CreateLinkedTokenSource(hostStopping);
        _native = native; _dialog = dialog; _nativeExit = native is null ? Task.CompletedTask : WatchNativeAsync();
    }

    public Task<SecretFieldStatus> ChangeAsync(Guid operation, long expectedRevision, SecretDisposition disposition, SecretValue? value)
    {
        if (expectedRevision is < 0 or > SecretFieldReference.MaximumRevision)
            throw new ArgumentException("control_token_revision_invalid");
        var key = SecretFieldKey.HostControlBearer(_instance.InstanceId);
        _ = SecretFieldStore.Identity(operation, key, expectedRevision, disposition);
        if (!Enum.IsDefined(disposition) || (disposition == SecretDisposition.Set) != (value is not null) || disposition == SecretDisposition.Keep)
            throw new ArgumentException("control_token_change_invalid");
        SecretValue? owned = null;
        var digest = "clear";
        value?.Use(secret =>
        {
            if (!ControlBearerVerifier.ValidToken(secret)) throw new ArgumentException("control_token_shape_invalid");
            owned = new(secret);
            Span<byte> bytes = stackalloc byte[256];
            var length = Encoding.ASCII.GetBytes(secret, bytes);
            digest = Convert.ToHexStringLower(SHA256.HashData(bytes[..length]));
            CryptographicOperations.ZeroMemory(bytes);
        });
        var request = expectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + disposition + "/" + digest;
        lock (_gate)
        {
            try
            {
                RequireLive();
                if (_pending.TryGetValue(operation, out var prior))
                    return prior.Request == request ? prior.Work : throw new OperationConflictException();
                if (_pending.Count >= 32) throw new IOException("control_token_confirmation_busy");
                var pending = new Pending(request, CancellationTokenSource.CreateLinkedTokenSource(_ending.Token));
                _pending.Add(operation, pending);
                pending.Work = ChangeCoreAsync(operation, expectedRevision, disposition, owned, pending);
                owned = null;
                return pending.Work;
            }
            finally { owned?.Dispose(); }
        }
    }

    public ControlTokenOperation? ReadOperation(Guid operation)
    {
        var key = SecretFieldKey.HostControlBearer(_instance.InstanceId);
        var subject = SecretFieldStore.Identity(operation, key, 0, SecretDisposition.Clear).SubjectId;
        _lease.VerifyHeld(_instance);
        var durable = _operations.Read(operation, subject);
        if (durable is not null)
            return durable.Kind == "secret.change" ? new(operation, durable.Status, true, durable.InitialJournalId) :
                throw new IOException("control_token_operation_unbound");
        lock (_gate)
            return _pending.ContainsKey(operation) ? new(operation, "preparing", false, null) : null;
    }

    public bool Cancel(Guid operation)
    {
        lock (_gate)
        {
            if (_disposed || !_pending.TryGetValue(operation, out var pending) || pending.EffectStarted) return false;
            pending.Cancellation.Cancel(); return true;
        }
    }

    private async Task<SecretFieldStatus> ChangeCoreAsync(Guid operation, long revision, SecretDisposition disposition,
        SecretValue? owned, Pending pending)
    {
        await Task.Yield();
        try
        {
            var key = SecretFieldKey.HostControlBearer(_instance.InstanceId);
            _lease.VerifyHeld(_instance);
            var replay = _store.Lookup(operation, key, revision, disposition, owned, pending.Cancellation.Token);
            if (replay is not null) return replay;
            var details = "Change this instance's remote-control bearer credential?\n\nInstance " +
                _instance.InstanceId.ToString("D") + "\nOperation " + operation.ToString("D") +
                "\nExpected credential revision " + revision.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "\n\nThis grants remote access only through the saved listener and server capability rules. " +
                "No credential value is displayed. Choose Cancel unless you requested this local change.";
            if (!await _dialogSlot.WaitAsync(0, pending.Cancellation.Token).ConfigureAwait(false))
                throw new IOException("control_token_confirmation_busy");
            bool approved;
            try
            {
                approved = await _dialog.ShowAsync(new(Guid.NewGuid(), "NexusPipeline control credential", details, "Allow change"),
                    TimeSpan.FromSeconds(60), pending.Cancellation.Token).ConfigureAwait(false);
            }
            finally { _dialogSlot.Release(); }
            lock (_gate)
            {
                RequireLive();
                if (!approved || pending.Cancellation.IsCancellationRequested)
                    throw new UnauthorizedAccessException("control_token_local_confirmation_required");
                pending.EffectStarted = true;
            }
            return await _secrets.ApplyAsync(operation, key, revision, disposition, owned, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            owned?.Dispose();
            lock (_gate) { _pending.Remove(operation); pending.Cancellation.Dispose(); }
        }
    }

    private void RequireLive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lease.VerifyHeld(_instance);
        if (_ending.IsCancellationRequested || _native is not null && !_native.IsLive)
            throw new UnauthorizedAccessException("control_token_local_context_closed");
    }

    private async Task WatchNativeAsync()
    {
        try { await _native!.WaitForExitAsync(_ending.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_ending.IsCancellationRequested) { return; }
        finally { _ending.Cancel(); }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] work;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _ending.Cancel();
            work = _pending.Values.Select(value => (Task)value.Work).ToArray();
        }
        foreach (var item in work)
        {
            try { await item.ConfigureAwait(false); }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { }
        }
        await _nativeExit.ConfigureAwait(false);
        _dialogSlot.Dispose(); _ending.Dispose();
    }

    private sealed class Pending(string request, CancellationTokenSource cancellation)
    {
        internal string Request { get; } = request;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal bool EffectStarted { get; set; }
        internal Task<SecretFieldStatus> Work { get; set; } = null!;
    }
}

public sealed record ControlTokenOperation(Guid OperationId, string Status, bool IsDurable, Guid? InitialJournalId);
