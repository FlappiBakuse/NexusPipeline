using System.Globalization;
using System.Buffers;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed class JournalStore
{
    private const int MaximumRevisions = JournalCapacityLimits.MaximumRevisions;
    private const long MaximumHistoryBytes = JournalCapacityLimits.MaximumHistoryBytes;
    private readonly RuntimeInstance _instance;
    private readonly ContractReader _contracts;
    private readonly object _gate = new();
    private JournalHistoryProtection? _protection;
    private readonly IJournalPublicationObserver? _publicationObserver;
    internal RuntimeInstance Instance => _instance;
    internal ContractReader Contracts => _contracts;
    internal Guid? ProtectedJournalId { get { lock (_gate) return _protection?.JournalId; } }

    internal IReadOnlyList<JournalDocument>? ReadProtectedHistory(Guid journalId)
    {
        lock (_gate) return _protection?.JournalId == journalId ? _protection.Read() : null;
    }

    public JournalStore(RuntimeInstance instance, ContractReader contracts)
    {
        var actual = new InstanceRoot(contracts).OpenExisting(instance.RuntimeRoot);
        if (instance.InstanceId != actual.InstanceId) throw new IOException("runtime_identity_mismatch");
        _instance = actual;
        _contracts = contracts;
    }

    internal JournalStore(RuntimeInstance instance, ContractReader contracts, IJournalPublicationObserver observer) : this(instance, contracts)
        => _publicationObserver = observer;

    internal IDisposable ProtectReadHistory(Guid journalId)
    {
        using var timing = StartupProfile.Measure("history.protect");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        lock (_gate)
        {
            if (_protection is not null) throw new IOException("journal_history_scope_already_active");
            using var publication = JournalPublicationGate.Acquire(_instance, journalId);
            var held = JournalHistoryProtection.TryAcquire(JournalDirectory(journalId));
            if (held is null) return new ProtectionScope(this, null);
            try
            {
                var documents = new List<JournalDocument>();
                ReadHistory(journalId, documents, held);
                held.Bind(_instance, documents); _protection = held;
                return new ProtectionScope(this, held);
            }
            catch { held.Dispose(); throw; }
        }
    }

    private void ReleaseProtection(JournalHistoryProtection held)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_protection, held)) return;
            _protection = null; held.Dispose();
        }
    }

    private sealed class ProtectionScope(JournalStore owner, JournalHistoryProtection? held) : IDisposable
    {
        public void Dispose() { if (held is not null) owner.ReleaseProtection(held); }
    }

    public JournalDocument Create(ReadOnlyMemory<byte> bytes)
    {
        lock (_gate)
        {
            var document = Parse(bytes);
            using var publication = JournalPublicationGate.Acquire(_instance, document.JournalId);
            if (document.Revision != 1 || document.Phase != JournalPhase.Prepared ||
                document.Root.GetProperty("receipts").GetArrayLength() != 0)
                throw new IOException("journal_initial_state_invalid");
            VerifyPlan(document);
            var parent = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(_instance.RuntimeRoot, "journals"));
            Directory.CreateDirectory(parent);
            var directory = JournalDirectory(document.JournalId);
            ExclusiveDirectory.Create(directory);
            WriteRevision(directory, document);
            return document;
        }
    }

    public JournalDocument Read(Guid journalId)
    {
        JournalReadScope.SelectActiveParent(_instance, _contracts, journalId);
        lock (_gate)
        {
            using var publication = JournalPublicationGate.Acquire(_instance, journalId);
            return ReadHistory(journalId);
        }
    }

    public IReadOnlyList<JournalDocument> ReadAllRevisions(Guid journalId)
    {
        JournalReadScope.SelectActiveParent(_instance, _contracts, journalId);
        lock (_gate)
        {
            using var publication = JournalPublicationGate.Acquire(_instance, journalId);
            var revisions = new List<JournalDocument>();
            ReadHistory(journalId, revisions);
            return revisions.AsReadOnly();
        }
    }

    public JournalDocument Append(Guid journalId, long expectedRevision, ReadOnlyMemory<byte> bytes,
        NoEffectEvidence? noEffect = null, bool hasVerifiedRecoveryEvidence = false)
    {
        JournalReadScope.SelectActiveParent(_instance, _contracts, journalId);
        var owner = JournalReadScope.WriteOwner(this, journalId) ?? this;
        return owner.AppendCore(journalId, expectedRevision, bytes, noEffect, hasVerifiedRecoveryEvidence, _publicationObserver);
    }

    private JournalDocument AppendCore(Guid journalId, long expectedRevision, ReadOnlyMemory<byte> bytes,
        NoEffectEvidence? noEffect, bool hasVerifiedRecoveryEvidence, IJournalPublicationObserver? observer)
    {
        lock (_gate)
        {
            using var publication = JournalPublicationGate.Acquire(_instance, journalId);
            if (_protection is { } protection && protection.JournalId == journalId && !protection.CanRetain(bytes.Length))
                ReleaseProtection(protection);
            var current = ReadHistory(journalId);
            if (current.Revision != expectedRevision) throw new IOException("journal_revision_conflict");
            if (current.Revision >= MaximumRevisions) throw new IOException("journal_history_limit_or_missing");
            var next = Parse(bytes);
            JournalDocument.ValidateSuccessor(current, next);
            if (current.Phase != next.Phase && !JournalTransitionPolicy.CanMove(current.Kind, current.Phase, next.Phase, noEffect, hasVerifiedRecoveryEvidence))
                throw new IOException("journal_transition_rejected");
            if (current.Phase == JournalPhase.Prepared && next.Phase == JournalPhase.Committed &&
                !next.Root.GetProperty("receipts").EnumerateArray().Any(receipt => receipt.GetProperty("effect").GetString() == "notExecuted"))
                throw new IOException("journal_no_effect_receipt_required");
            VerifyPlan(next);
            var totalBytes = _protection?.JournalId == journalId ? _protection.TotalBytes :
                Directory.EnumerateFiles(JournalDirectory(journalId)).Sum(path => new FileInfo(path).Length);
            if (checked(totalBytes + next.Export().Length) > MaximumHistoryBytes) throw new IOException("journal_history_byte_limit");
            WriteRevision(JournalDirectory(journalId), next, observer);
            if (_protection?.JournalId == journalId) _protection.Include(next);
            return next;
        }
    }

    private JournalDocument ReadHistory(Guid journalId, List<JournalDocument>? snapshots = null,
        JournalHistoryProtection? initialProtection = null)
    {
        var protectedDocuments = ReadProtectedHistory(journalId) ?? JournalReadScope.Read(this, journalId);
        if (protectedDocuments is not null)
        {
            snapshots?.AddRange(protectedDocuments);
            return protectedDocuments[^1];
        }
        var directory = JournalDirectory(journalId);
        var files = Directory.EnumerateFileSystemEntries(directory).Take(MaximumRevisions + 1).Order(StringComparer.Ordinal).ToArray();
        if (files.Length is 0 or > MaximumRevisions) throw new IOException("journal_history_limit_or_missing");
        JournalDocument? previous = null;
        long total = 0;
        foreach (var file in files)
        {
            PhysicalPathPolicy.NormalizeWithoutLinks(file);
            var expected = (previous?.Revision ?? 0) + 1;
            if (Path.GetFileName(file) != FileName(expected)) throw new IOException("journal_unknown_entry_or_revision_gap");
            using var owned = initialProtection is null ? new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            var input = owned ?? initialProtection!.BorrowRevision(checked((int)expected - 1), Path.GetFileName(file));
            total = checked(total + input.Length);
            if (input.Length > StrictJson.MaximumBytes || total > MaximumHistoryBytes) throw new IOException("journal_history_byte_limit");
            var length = checked((int)input.Length);
            var buffer = ArrayPool<byte>.Shared.Rent(length);
            JournalDocument current;
            try
            {
                input.ReadExactly(buffer.AsSpan(0, length));
                current = JournalDocument.ReadBorrowed(buffer.AsMemory(0, length), _contracts);
                if (current.InstanceId != _instance.InstanceId) throw new IOException("journal_instance_mismatch");
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            if (current.JournalId != journalId || current.Revision != expected) throw new IOException("journal_revision_conflict");
            if (previous is null)
            {
                if (current.Phase != JournalPhase.Prepared || current.Root.GetProperty("receipts").GetArrayLength() != 0)
                    throw new IOException("journal_initial_state_invalid");
            }
            else JournalDocument.ValidateSuccessor(previous, current);
            snapshots?.Add(current);
            previous = current;
        }
        // Protected admission binds and hashes the plan before publishing the retained history.
        if (initialProtection is null) VerifyPlan(previous!);
        return previous!;
    }

    private JournalDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        var document = JournalDocument.Read(bytes, _contracts);
        if (document.InstanceId != _instance.InstanceId) throw new IOException("journal_instance_mismatch");
        return document;
    }

    private void VerifyPlan(JournalDocument document)
    {
        if (_protection?.JournalId == document.JournalId)
        { _protection.RequirePlan(document); return; }
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(_instance.RuntimeRoot, document.PlanPath));
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > StrictJson.MaximumBytes || Convert.ToHexStringLower(SHA256.HashData(input)) != document.PlanDigest)
            throw new IOException("journal_plan_digest_mismatch");
    }

    internal void VerifyReferencedPlan(RegisteredJournal registered)
    {
        if (registered.PlanPath != $"blobs/plans/{registered.PlanDigest}.json") throw new IOException("journal_plan_reference_invalid");
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(_instance.RuntimeRoot, registered.PlanPath));
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > StrictJson.MaximumBytes || Convert.ToHexStringLower(SHA256.HashData(input)) != registered.PlanDigest)
            throw new IOException("journal_plan_digest_mismatch");
    }

    private string JournalDirectory(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Journal identity is required");
        return PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(_instance.RuntimeRoot, "journals", id.ToString("D")));
    }

    private static string FileName(long revision) => revision.ToString("D10", CultureInfo.InvariantCulture) + ".json";

    private void WriteRevision(string directory, JournalDocument document, IJournalPublicationObserver? observer = null)
    {
        using var output = new FileStream(Path.Combine(directory, FileName(document.Revision)), FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        (observer ?? _publicationObserver)?.RevisionCreated(output.Name);
        output.Write(document.Export());
        output.Flush(flushToDisk: true);
    }
}
