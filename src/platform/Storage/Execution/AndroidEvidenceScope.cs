using NexusPipeline.Extensibility.Protocol;
using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class AndroidEvidenceScope(ExecutionPlan ownedTotal, StateDatabase? database = null) : IDisposable
{
    private readonly ExecutionSqlScope? _sql = database is null ? null : new(database);

    internal T ReadSql<T>(StateDatabase requestedDatabase, Func<SqliteConnection, T> read)
    {
        if (_sql is not null) return _sql.Read(requestedDatabase, read);
        using var connection = DatabaseConnections.Open(requestedDatabase.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        return read(connection);
    }

    public void Dispose() => _sql?.Dispose();
    private readonly record struct DocumentKey(Type Type, BlobReference Reference);
    private readonly record struct RequestKey(ExecutionTargetBinding Binding, string Subject, int Sequence,
        AndroidCommandPurpose Purpose, ExecutionAndroidAction Action);
    private sealed record Document(byte[] Original, object Value);
    private readonly Dictionary<DocumentKey, Document> _documents = [];
    private readonly Dictionary<RequestKey, AndroidCommandRequest> _derived = [];
    private readonly Dictionary<AndroidCommandRequest, string> _digests = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<AndroidCommandRequest> _frozenRequests = new(ReferenceEqualityComparer.Instance);
    private int _originalBytes;

    internal T Decode<T>(BlobReference reference, byte[] currentBytes) where T : class
    {
        var type = typeof(T);
        if (type != typeof(AndroidCommandRequest) && type != typeof(AndroidControlPlan) &&
            type != typeof(AndroidCommandObservation) && type != typeof(AndroidCommandCollection) &&
            type != typeof(AndroidStartupDocument) && type != typeof(AndroidCollectedDocument))
            return AndroidControlCodec.Decode<T>(currentBytes);
        var key = new DocumentKey(type, reference);
        if (_documents.TryGetValue(key, out var prior) && prior.Original.AsSpan().SequenceEqual(currentBytes)) return (T)prior.Value;
        var value = AndroidControlCodec.Decode<T>(currentBytes);
        if (value is AndroidCommandRequest request) value = (T)(object)Freeze(request);
        if (!_documents.ContainsKey(key) && _documents.Count < 128 && _originalBytes + currentBytes.Length <= 1024 * 1024)
        {
            _documents.Add(key, new(currentBytes.ToArray(), value)); _originalBytes += currentBytes.Length;
            if (value is AndroidCommandRequest frozen) _frozenRequests.Add(frozen);
        }
        return value;
    }

    internal AndroidCommandRequest Derive(ExecutionPlan total, AndroidCommandRegistration registration,
        string freshlyReadSubject)
    {
        if (!ReferenceEquals(total, ownedTotal)) throw new IOException("execution_android_read_scope_unbound");
        var key = new RequestKey(registration.Binding, freshlyReadSubject, registration.Sequence, registration.Purpose, registration.Action);
        if (_derived.TryGetValue(key, out var prior)) return prior;
        var value = Freeze(AndroidCommandIdentity.Derive(total, registration.Binding, freshlyReadSubject,
            registration.Sequence, registration.Purpose, registration.Action));
        if (_derived.Count < 128) { _derived.Add(key, value); _frozenRequests.Add(value); }
        return value;
    }

    private static AndroidCommandRequest Freeze(AndroidCommandRequest request) =>
        request.Tool is not { RuntimeFiles: { } files } tool ? request :
            request with { Tool = tool with { RuntimeFiles = Array.AsReadOnly(files.ToArray()) } };

    internal string Digest(AndroidCommandRequest immutableRequest)
    {
        var frozen = _frozenRequests.Contains(immutableRequest);
        if (frozen && _digests.TryGetValue(immutableRequest, out var prior)) return prior;
        var value = AndroidCommandIdentity.Digest(immutableRequest);
        if (frozen && _digests.Count < 256) _digests.Add(immutableRequest, value);
        return value;
    }
}
