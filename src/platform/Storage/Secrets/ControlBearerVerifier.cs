using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
public sealed class ControlBearerVerifier : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly SecretFieldStore _secrets;
    private readonly IRecoveryIndex _recovery;
    private readonly SecretFieldService _service;
    private readonly object _gate = new();
    private LoadedCredential? _loaded;
    private bool _disposed;
    private bool _mutating;
    private readonly AsyncLocal<ControlBearerRequest?> _requests = new();
    private readonly AsyncLocal<ControlPublicationScope?> _publications = new();
    internal Action? ProducerRead { get; init; }

    public ControlBearerVerifier(RuntimeInstance instance, StateDatabase database, InstanceLease lease, IRecoveryIndex recovery,
        SecretFieldService service)
    {
        lease.VerifyHeld(instance);
        _secrets = new(instance, database);
        _instance = instance; _database = database; _lease = lease; _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _service = service;
        service.BindControlBearer(instance, database, lease, this);
    }

    public VerifiedControlBearer? Verify(ReadOnlySpan<char> presented)
    {
        _lease.VerifyHeld(_instance);
        if (!ValidToken(presented)) return null;
        Span<byte> utf8 = stackalloc byte[256];
        var count = Encoding.ASCII.GetBytes(presented, utf8);
        var candidate = SHA256.HashData(utf8[..count]);
        try
        {
            return _lease.PublishExecution<VerifiedControlBearer?>(() =>
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    var key = SecretFieldKey.HostControlBearer(_instance.InstanceId);
                    SecretFieldReference reference;
                    using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true))
                        reference = SecretFieldRows.ReadField(connection, null, key);
                    if (reference.SecretId is null) { Invalidate(); return null; }
                    LoadedCredential loaded;
                    try { loaded = Load(reference); }
                    catch (IOException error) when (RecoveryBlocked(error)) { throw; }
                    catch { Invalidate(); throw; }
                    if (!CryptographicOperations.FixedTimeEquals(candidate, loaded.Digest)) return null;
                    return new(this, reference);
                }
            });
        }
        finally { CryptographicOperations.ZeroMemory(utf8); CryptographicOperations.ZeroMemory(candidate); }
    }

    internal void RequireCurrent(VerifiedControlBearer credential, SecretFieldReference reference)
    {
        void Check()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _lease.VerifyHeld(_instance);
                try { _ = Load(reference, credential); }
                catch (IOException error) when (RecoveryBlocked(error)) { throw; }
                catch { Invalidate(); throw; }
            }
        }
        if (_publications.Value?.Allows(credential, _requests.Value, _recovery.Current) == true)
        {
            _lease.VerifyHeld(_instance); Check(); return;
        }
        _lease.PublishExecution(Check);
    }

    internal IDisposable EnterRequest(VerifiedControlBearer credential, SecretFieldReference reference)
    {
        RequireCurrent(credential, reference);
        var previous = _requests.Value;
        ControlBearerRequest? request = null;
        request = new(credential, previous, () => { if (ReferenceEquals(_requests.Value, request)) _requests.Value = previous; });
        _requests.Value = request; return request;
    }

    internal ControlPublicationScope? BeginPublication(ResourceOperationLease root, ResourceLeaseManager resources, string extensionId)
    {
        var request = _requests.Value;
        if (request is null) return null;
        var previous = _publications.Value;
        ControlPublicationScope? scope = null;
        scope = _lease.PublishExecution(() =>
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var loaded = Load(request.Credential.Reference, request.Credential);
                var original = _recovery.Current;
                if (!request.IsActive || !original.IsReady || !ReferenceEquals(original, loaded.Recovery))
                    throw new IOException("control_credential_recovery_unverified");
                var held = root.Borrow(resources, root.OperationId,
                    [new("extension-data:" + extensionId, ResourceClaimMode.Mutation), new("secrets:fields", ResourceClaimMode.Mutation)]);
                try
                {
                    return new ControlPublicationScope(request, held, original, () => _recovery.Current,
                        () => { if (ReferenceEquals(_publications.Value, scope)) _publications.Value = previous; });
                }
                catch { held.Dispose(); throw; }
            }
        });
        // The publication queue carries a copied ExecutionContext; activate the scope on its caller.
        _publications.Value = scope; return scope;
    }

    private bool RecoveryBlocked(IOException error) => error.Message == "control_credential_recovery_unverified" && !_recovery.Current.IsReady;

    internal SecretValue ReadLocal()
    {
        return _lease.PublishExecution(() =>
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var key = SecretFieldKey.HostControlBearer(_instance.InstanceId);
                SecretFieldReference reference;
                using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true))
                    reference = SecretFieldRows.ReadField(connection, null, key);
                try { _ = Load(reference); return _secrets.ReadControlBearer(reference); }
                catch { Invalidate(); throw; }
            }
        });
    }

    private LoadedCredential Load(SecretFieldReference reference, VerifiedControlBearer? credential = null)
    {
        if (_mutating) throw new IOException("control_credential_change_pending");
        var generation = _recovery.Current;
        var continuing = !generation.IsReady && credential is not null &&
            _publications.Value?.Allows(credential, _requests.Value, generation) == true;
        if (!generation.IsReady && !continuing) throw new IOException("control_credential_recovery_unverified");
        var producer = RequireProducer(reference, verifyBytes: false, trustedPlan: _loaded?.Reference == reference ? _loaded.Plan : null);
        if (_loaded is { } current && current.Reference == reference &&
            (ReferenceEquals(generation, current.Recovery) || continuing) && current.Producer == Stamp(producer))
        { current.Payload.RequireCurrent(); return current; }
        if (continuing) throw new IOException("control_credential_producer_changed");
        RequireRecovery(generation, producer.Journal.JournalId);
        Invalidate();
        var held = new List<IDisposable>();
        byte[]? digest = null;
        try
        {
            var payload = _secrets.HoldControlBearer(reference);
            held.Add(payload);
            producer = RequireProducer(reference, held: held);
            using var value = _secrets.ReadControlBearer(reference);
            value.Use(secret =>
            {
                if (!ValidToken(secret)) throw new IOException("control_credential_value_invalid");
                Span<byte> stored = stackalloc byte[256];
                try
                {
                    var count = Encoding.ASCII.GetBytes(secret, stored);
                    digest = SHA256.HashData(stored[..count]);
                }
                finally { CryptographicOperations.ZeroMemory(stored); }
            });
            if (!ReferenceEquals(generation, _recovery.Current) ||
                Stamp(RequireProducer(reference, verifyBytes: false, trustedPlan: producer.Plan)) != Stamp(producer))
                throw new IOException("control_credential_producer_changed");
            payload.RequireCurrent();
            return _loaded = new(reference, Stamp(producer), generation, digest!, held, producer.Plan!, payload);
        }
        catch
        {
            foreach (var item in held) item.Dispose();
            if (digest is not null) CryptographicOperations.ZeroMemory(digest);
            throw;
        }
    }

    private void RequireRecovery(RecoveryProjection generation, Guid producer)
    {
        foreach (var group in generation.Isolations.GroupBy(item => item.JournalId))
        {
            if (group.Key == producer || group.Any(item => item.ReasonCode != "recovery_pending"))
                throw new IOException("control_credential_recovery_unverified");
            // An active journal owned by this Host is a resource hold, not an abandoned or quarantined writer.
            new JournalWriterStore(_database).RequireRegisteredExecutor(group.Key);
        }
    }

    private (RegisteredJournal Journal, NexusPipeline.Platform.Serialization.Protocol.SecretPreparationPlan? Plan) RequireProducer(
        SecretFieldReference reference, bool verifyBytes = true, List<IDisposable>? held = null,
        NexusPipeline.Platform.Serialization.Protocol.SecretPreparationPlan? trustedPlan = null)
    {
        var key = SecretFieldKey.HostControlBearer(_instance.InstanceId);
        if (reference.SecretId is not { } secret || reference.Revision < 1)
            throw new IOException("control_credential_producer_unbound");
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        SecretFieldRows.RequireReference(connection, transaction, key, reference);
        Guid operation, journalId;
        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT p.preparation_operation_id,s.journal_id,o.subject_id,o.request_digest,o.operation_kind,o.status," +
                "m.result_revision,m.result_secret_id,s.state,p.state,m.result_readable FROM secret_payloads p " +
                "JOIN secret_preparations s ON s.operation_id=p.preparation_operation_id AND s.candidate_id=p.secret_id " +
                "JOIN operations o ON o.operation_id=p.preparation_operation_id JOIN secret_mutations m ON m.operation_id=o.operation_id " +
                "WHERE p.secret_id=$id;";
            query.Parameters.AddWithValue("$id", secret.ToString("D"));
            using var row = query.ExecuteReader();
            if (!row.Read()) throw new IOException("control_credential_producer_unbound");
            operation = Guid.ParseExact(row.GetString(0), "D");
            journalId = Guid.ParseExact(row.GetString(1), "D");
            var expected = SecretFieldOperation.Identity(operation, key, reference.Revision - 1, SecretDisposition.Set);
            if (row.GetString(0) != operation.ToString("D") || row.GetString(1) != journalId.ToString("D") ||
                row.GetString(2) != expected.SubjectId || row.GetString(3) != expected.RequestDigest ||
                row.GetString(4) != expected.Kind || row.GetString(5) != "completed" ||
                row.GetInt64(6) != reference.Revision || row.GetString(7) != secret.ToString("D") ||
                (SecretPreparationState)row.GetInt32(8) != SecretPreparationState.Published || row.GetString(9) != "committed" || !row.GetBoolean(10))
                throw new IOException("control_credential_producer_unbound");
        }
        var registered = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, journalId);
        if (registered.Kind != JournalKind.SecretPrepare || registered.Phase != JournalPhase.Committed ||
            registered.OperationId != operation || registered.PlanPath != $"blobs/plans/{registered.PlanDigest}.json")
            throw new IOException("control_credential_producer_unbound");
        if (!verifyBytes)
        {
            if (trustedPlan is not null) SecretPreparationRows.VerifyPlan(connection, transaction, trustedPlan);
            return (registered, trustedPlan);
        }
        ProducerRead?.Invoke();
        if (held is not null)
        {
            var directory = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(_instance.RuntimeRoot, "journals", journalId.ToString("D")));
            held.Add(StableDirectoryLease.Acquire(directory));
            foreach (var file in Directory.EnumerateFileSystemEntries(directory).Take(JournalCapacityLimits.MaximumRevisions + 1))
                held.Add(new FileStream(PhysicalPathPolicy.NormalizeWithoutLinks(file), FileMode.Open, FileAccess.Read, FileShare.Read));
        }
        var contracts = new ContractReader();
        var history = new JournalStore(_instance, contracts).ReadAllRevisions(journalId);
        var latest = history[^1];
        if (latest.Root.GetProperty("operationId").GetGuid() != operation || latest.Kind != registered.Kind ||
            latest.Revision != registered.Revision || latest.Phase != registered.Phase || SecretPreparationJournal.NoEffects(latest) ||
            latest.PlanDigest != registered.PlanDigest || latest.PlanPath != registered.PlanPath)
            throw new IOException("control_credential_producer_unbound");
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(_instance.RuntimeRoot, registered.PlanPath));
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        held?.Add(input);
        try
        {
            if (input.Length is < 1 or > StrictJson.MaximumBytes ||
                Convert.ToHexStringLower(SHA256.HashData(input)) != registered.PlanDigest)
                throw new IOException("control_credential_producer_unbound");
            input.Position = 0;
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            var original = new PreparationPlanCodec(_instance, contracts, _database).Decode(bytes);
            var frozen = original.ReadWire();
            var binding = new SecretPreparationJournal(_instance, contracts, _database, original,
                new(registered.PlanPath, input.Length, registered.PlanDigest));
            foreach (var document in history) binding.Verify(document);
            if (frozen.OperationId != operation.ToString("D") || frozen.JournalId != journalId.ToString("D") ||
                SecretPreparationRows.Key(frozen) != key || !registered.ResourceKeys.SequenceEqual(frozen.ResourceKeys) ||
                frozen.ExpectedRevision + 1 != reference.Revision ||
                frozen.Candidate.SecretId != secret.ToString("D"))
                throw new IOException("control_credential_producer_unbound");
            SecretPreparationRows.VerifyPlan(connection, transaction, frozen);
            transaction.Commit();
            return (registered, frozen);
        }
        finally { if (held is null) input.Dispose(); }
    }

    private static string Stamp((RegisteredJournal Journal, NexusPipeline.Platform.Serialization.Protocol.SecretPreparationPlan? Plan) proof)
    {
        var value = proof.Journal;
        return string.Join("/", value.OperationId, value.JournalId, value.Revision, value.PlanPath, value.PlanDigest,
            string.Join(";", value.ResourceKeys));
    }

    private void Invalidate() { _loaded?.Dispose(); _loaded = null; }

    internal IDisposable BeginMutation()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_mutating) throw new IOException("control_credential_change_pending");
            _mutating = true;
            Invalidate();
            return new MutationScope(this);
        }
    }

    private sealed class MutationScope(ControlBearerVerifier owner) : IDisposable
    {
        private ControlBearerVerifier? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } current)
                lock (current._gate) current._mutating = false;
        }
    }

    public void Dispose()
    { lock (_gate) { if (_disposed) return; _disposed = true; Invalidate(); _service.ReleaseControlBearer(this); } }

    private sealed record LoadedCredential(SecretFieldReference Reference, string Producer, RecoveryProjection? Recovery,
        byte[] Digest, List<IDisposable> Held, NexusPipeline.Platform.Serialization.Protocol.SecretPreparationPlan Plan,
        SecretPayloadLease Payload) : IDisposable
    {
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Digest);
            for (var index = Held.Count - 1; index >= 0; index--) Held[index].Dispose();
        }
    }

    internal static bool ValidToken(ReadOnlySpan<char> value)
    {
        if (value.Length is < 32 or > 256) return false;
        foreach (var character in value)
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return false;
        return true;
    }
}

[SupportedOSPlatform("windows")]
public sealed class VerifiedControlBearer
{
    private readonly ControlBearerVerifier _owner;
    private readonly SecretFieldReference _reference;
    internal SecretFieldReference Reference => _reference;
    internal VerifiedControlBearer(ControlBearerVerifier owner, SecretFieldReference reference)
    { _owner = owner; _reference = reference; }
    public void RequireCurrent() => _owner.RequireCurrent(this, _reference);
    public void RequireSameCurrent(VerifiedControlBearer original)
    {
        if (original is null || !ReferenceEquals(_owner, original._owner) || _reference != original._reference)
            throw new UnauthorizedAccessException("control_credential_identity_changed");
        RequireCurrent();
    }
    public IDisposable EnterRequest() => _owner.EnterRequest(this, _reference);
}
