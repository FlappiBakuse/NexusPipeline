using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record RawReplayResult(bool Continuous, BusinessOutcome Business, string? ReasonCode);

internal sealed class AttemptReadSession : IDisposable
{
    private readonly object _gate = new();
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ImmutableBlobStore _blobs;
    private readonly ContractReader _contracts;
    private readonly JournalStore _journals;
    private readonly IDisposable _history;
    private readonly AttemptBoundInputs _inputs;
    private readonly BlobReference _planReference;
    private readonly ExecutionPlan _total;
    internal AttemptReadMemo ParsedHistory { get; }
    private readonly DatabaseValidationLease _cancellationSql;
    private ExecutionCancellationIntent? _close;
    private readonly Action _released;
    private readonly DirectoryAttributeProtection _paths;
    private readonly Dictionary<string, (IDisposable Lease, long Bytes, LinkedListNode<string> Node)> _verified = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _recent = new();
    private long _verifiedBytes;
    private bool _cacheable = true;
    private readonly Dictionary<BlobReference, FileStream> _rawFiles = [];
    private readonly Dictionary<(StepEvidenceBinding, BlobReference), (AutomationJudgement Definition, RawReplayResult Result)> _replays = [];
    private AutomationBundle? _definitions;
    private FileStream? _definitionFile;
    private BoundPreparedAttempt? _bound;
    private AttemptHistoryCursor? _prefix;
    private long _revision;
    private long _rawBytes;
    private bool _disposed;
    internal Guid JournalId { get; }
    internal JournalStore Journals => _journals;

    internal AttemptReadSession(RuntimeInstance instance, StateDatabase database, ContractReader contracts, Guid journal, Action released,
        bool requireFreshInventory)
    {
        _instance = instance; _database = database; _contracts = contracts; JournalId = journal; _released = released;
        _blobs = new(instance, contracts); _journals = new(instance, contracts);
        var registered = new RecoveryJournalRegistry(database).ReadJournal(journal);
        _planReference = AttemptPreparedBinding.Reference(instance, registered);
        _inputs = AttemptPreparedBinding.ReadPlanInputs(instance, contracts, registered);
        _total = _inputs.Total;
        ParsedHistory = new(_total);
        try
        {
            _paths = DirectoryAttributeProtection.Acquire(instance);
            try
            {
                _history = _journals.ProtectReadHistory(journal, requireFreshInventory);
                try
                {
                    _cancellationSql = new(database, "attempt.cancel.validate", DatabaseDomain.Runtime);
                }
                catch { _history.Dispose(); throw; }
            }
            catch { _paths.Dispose(); throw; }
        }
        catch { _inputs.Dispose(); throw; }
    }

    internal bool Matches(ExecutionPlan total) => !_disposed && _total.RunId == total.RunId &&
        _total.DefinitionBundle.RelativePath == total.DefinitionBundle.RelativePath &&
        _total.DefinitionBundle.SizeBytes == total.DefinitionBundle.SizeBytes &&
        _total.DefinitionBundle.Sha256 == total.DefinitionBundle.Sha256;

    internal ExecutionCancellationIntent? ReadClose(RegisteredJournal parent)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (parent.JournalId != JournalId || parent.OperationId.ToString("D") != _inputs.Plan.OperationId)
                throw new IOException("attempt_read_context_unbound");
            using var paths = DirectoryAttributeScope.Enter(_paths);
            _cancellationSql.RequireCurrent((connection, transaction) =>
                _close = ExecutionCancellationRows.Read(connection, transaction, parent, _inputs.Plan));
            return _close;
        }
    }

    internal BoundPreparedAttempt ReadBinding(Func<JournalStore?, AttemptBoundInputs?, BoundPreparedAttempt> read)
    {
        lock (_gate)
        {
            if (_disposed) return read(null, null);
            _cacheable = true;
            using var paths = DirectoryAttributeScope.Enter(_paths);
            using var instance = _database.RuntimeLease is { } owner
                ? InstanceReadScope.Hold(_instance, owner) : InstanceReadScope.Hold(_instance, _contracts);
            using var blobs = BlobReadScope.Capture(_instance, RetainVerified);
            using var selected = JournalReadScope.Hold(_journals, JournalId);
            var history = _journals.ReadAllRevisions(JournalId);
            if (!_cacheable || _journals.ProtectedJournalId != JournalId) return read(_journals, _inputs);
            if (_revision != history[^1].Revision)
            { _bound = null; _revision = history[^1].Revision; }
            if (_bound is not null) RequireBinding(new RecoveryJournalRegistry(_database).ReadJournal(JournalId));
            if (_bound is not null) return _bound;
            var current = read(_journals, _inputs) ?? throw new IOException("attempt_read_binding_missing");
            if (_cacheable) _bound = current;
            return current;
        }
    }

    private void RequireBinding(RegisteredJournal journal)
    {
        if (journal.PlanPath != _planReference.RelativePath || journal.PlanDigest != _planReference.Sha256)
            throw new IOException("attempt_read_binding_changed");
        AttemptPreparedBinding.RequireRegistration(journal, _inputs.Plan);
    }

    internal AttemptHistoryCursor? ReadHistoryPrefix(IReadOnlyList<JournalDocument> history, EffectGateState gate)
    {
        lock (_gate)
        {
            if (_disposed || !_cacheable || _journals.ProtectedJournalId != JournalId) return null;
            return _prefix?.Fork(history, gate);
        }
    }

    internal void RememberHistory(AttemptHistoryCursor cursor, IReadOnlyList<JournalDocument> history, EffectGateState gate)
    {
        lock (_gate)
        {
            if (_disposed || !_cacheable || _journals.ProtectedJournalId != JournalId) { _prefix = null; return; }
            cursor.Complete(history, gate); _prefix = cursor;
        }
    }

    internal (ExecutionPlan Total, StepWriterState Job, ProcessIdentity? Root)? ReadContext(
        StepEvidenceReader reader, RegisteredJournal journal, StepEvidenceBinding binding, string role)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (journal.JournalId != JournalId) throw new IOException("attempt_read_context_unbound");
            using var paths = DirectoryAttributeScope.Enter(_paths);
            using var selected = JournalReadScope.Hold(_journals, JournalId);
            return reader.ContextFromValidatedRead(binding, journal, _inputs.Plan, _total, _journals.Read(JournalId), role);
        }
    }

    private void RetainVerified(string path)
    {
        if (_verified.TryGetValue(path, out var prior))
        { _recent.Remove(prior.Node); _recent.AddLast(prior.Node); return; }
        var held = BlobReadStream.Retain(path, out var reference);
        if (reference.SizeBytes > 16777216)
        { held.Dispose(); _cacheable = false; _bound = null; _prefix = null; return; }
        try
        {
            while (_verified.Count >= 256 || reference.SizeBytes > 16777216 - _verifiedBytes)
            {
                var oldest = _recent.First!;
                var removed = _verified[oldest.Value];
                _recent.RemoveFirst(); _verified.Remove(oldest.Value);
                _verifiedBytes -= removed.Bytes; removed.Lease.Dispose();
                _cacheable = false; _bound = null; _prefix = null;
            }
            _paths.Include([Path.GetDirectoryName(path)!]);
            _verified.Add(path, (held, reference.SizeBytes, _recent.AddLast(path))); _verifiedBytes += reference.SizeBytes;
        }
        catch { held.Dispose(); throw; }
    }

    internal AutomationBundle? ReadDefinitions()
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (_definitions is not null) return _definitions;
            var content = _inputs.Total.DefinitionBundle;
            var input = _blobs.OpenVerified(new(content.RelativePath, content.SizeBytes, content.Sha256));
            try
            {
                var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
                var value = _contracts.Read<AutomationBundle>(bytes, "automation-bundle.schema.json");
                _definitions = value; _definitionFile = input;
                return value;
            }
            catch { input.Dispose(); throw; }
        }
    }

    internal AttemptMetadataPlan? ReadPlanMetadata(RegisteredJournal journal, BlobReference reference)
    {
        lock (_gate)
        {
            if (_disposed || reference != _planReference) return null;
            AttemptPreparedBinding.RequireRegistration(journal, _inputs.Plan);
            return new(_inputs.Plan, _total);
        }
    }

    internal RawReplayResult Replay(StepEvidenceBinding binding, BlobReference reference, RawOutputManifest raw, AutomationJudgement definition)
    {
        lock (_gate)
        {
            var key = (binding, reference);
            if (_replays.TryGetValue(key, out var previous) && previous.Definition.Language == definition.Language &&
                previous.Definition.Script == definition.Script &&
                previous.Definition.SuccessKeywords.IsDefault == definition.SuccessKeywords.IsDefault &&
                previous.Definition.FailureKeywords.IsDefault == definition.FailureKeywords.IsDefault &&
                previous.Definition.SuccessKeywords.AsSpan().SequenceEqual(definition.SuccessKeywords.AsSpan()) &&
                previous.Definition.FailureKeywords.AsSpan().SequenceEqual(definition.FailureKeywords.AsSpan())) return previous.Result;
            var added = raw.Parts.Select(item => item.Content).Distinct().Where(item => !_rawFiles.ContainsKey(item)).ToArray();
            var retain = !_disposed && (_replays.ContainsKey(key) || _replays.Count < 64) && added.Length <= 128 - _rawFiles.Count &&
                added.Sum(item => item.SizeBytes) <= 16777216 - _rawBytes;
            var opened = new List<(BlobReference Reference, FileStream Input)>();
            try
            {
                if (retain)
                    foreach (var part in added) opened.Add((part, _blobs.OpenVerified(part)));
                var judgement = new AttemptBusinessJudgement(binding.AttemptId, definition);
                var continuous = RawOutputReplay.Read(_blobs, raw, line => judgement.ObserveLine(judgement.InputIdentity, line));
                var result = new RawReplayResult(continuous, judgement.Business, judgement.Decision?.ReasonCode);
                if (retain)
                {
                    foreach (var part in opened) { _rawFiles.Add(part.Reference, part.Input); _rawBytes += part.Reference.SizeBytes; }
                    opened.Clear(); _replays[key] = (definition, result);
                }
                return result;
            }
            finally { foreach (var part in opened) part.Input.Dispose(); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _definitionFile?.Dispose();
                foreach (var file in _rawFiles.Values) file.Dispose();
                _rawFiles.Clear(); _replays.Clear(); _bound = null; _prefix = null; _definitions = null;
                foreach (var held in _verified.Values) held.Lease.Dispose();
                _verified.Clear(); _recent.Clear();
                ParsedHistory.Dispose();
                _cancellationSql.Dispose(); _history.Dispose(); _inputs.Dispose(); _paths.Dispose();
            }
            finally { _released(); }
        }
    }
}
