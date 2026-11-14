using System.Text.Json;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed class JournalDocument
{
    private const int MaximumMemoDocuments = 1024;
    private const int MaximumMemoOriginalBytes = 4 * 1024 * 1024;
    private readonly record struct TextKey(int Length, string Sha256);
    private readonly record struct SuccessorKey(TextKey Before, TextKey After);
    private const int MaximumSuccessorProofs = 8192;
    private static readonly object SuccessorGate = new();
    private static readonly HashSet<SuccessorKey> Successors = [];
    private static readonly Queue<SuccessorKey> SuccessorOrder = [];
    private static readonly object MemoGate = new();
    private static readonly Dictionary<TextKey, JournalDocument> Memo = [];
    private static readonly Queue<TextKey> MemoOrder = [];
    private static int _memoOriginalBytes;
    private readonly byte[] _bytes;
    private readonly TextKey _textKey;
    internal JsonElement Root { get; }
    public Guid InstanceId { get; }
    public Guid JournalId { get; }
    public long Revision { get; }
    public JournalKind Kind { get; }
    public JournalPhase Phase { get; }
    public string PlanDigest { get; }
    public string PlanPath { get; }
    public byte[] Export() => _bytes.ToArray();
    internal int ByteLength => _bytes.Length;
    internal void WriteTo(Stream output) => output.Write(_bytes);
    internal bool HasSameBytes(JournalDocument other) => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    private JournalDocument(byte[] bytes, JsonElement root, TextKey textKey)
    {
        _bytes = bytes; _textKey = textKey;
        Root = root;
        InstanceId = root.GetProperty("instanceId").GetGuid();
        JournalId = root.GetProperty("journalId").GetGuid();
        Revision = root.GetProperty("revision").GetInt64();
        Kind = ParseEnum<JournalKind>(root, "operationKind");
        Phase = ParseEnum<JournalPhase>(root, "phase");
        PlanDigest = root.GetProperty("planDigest").GetString()!;
        PlanPath = root.GetProperty("planPath").GetString()!;
    }

    public static JournalDocument Read(ReadOnlyMemory<byte> bytes, ContractReader contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        if (bytes.Length is < 1 or > StrictJson.MaximumBytes) throw new JsonException("invalid_json_size");
        var ownedBytes = bytes.ToArray();
        return ReadOwned(ownedBytes, contracts);
    }

    internal static JournalDocument ReadBorrowed(ReadOnlyMemory<byte> bytes, ContractReader contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        if (bytes.Length is < 1 or > StrictJson.MaximumBytes) throw new JsonException("invalid_json_size");
        var key = new TextKey(bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes.Span)));
        lock (MemoGate)
            if (Memo.TryGetValue(key, out var cached) && cached._bytes.AsSpan().SequenceEqual(bytes.Span)) return cached;
        return ReadOwned(bytes.ToArray(), contracts);
    }

    private static JournalDocument ReadOwned(byte[] ownedBytes, ContractReader contracts)
    {
        var key = new TextKey(ownedBytes.Length, Convert.ToHexStringLower(SHA256.HashData(ownedBytes)));
        // Reusing immutable parsed text grants no authority over freshly read files, SQL or native ownership.
        lock (MemoGate)
            if (Memo.TryGetValue(key, out var cached) && cached._bytes.AsSpan().SequenceEqual(ownedBytes)) return cached;
        using var timing = StartupProfile.Measure("history.text.parse");
        using var document = contracts.Read(ownedBytes, "execution-journal.schema.json");
        var result = new JournalDocument(ownedBytes, document.RootElement.Clone(), key);
        if (result.InstanceId == Guid.Empty || result.JournalId == Guid.Empty ||
            result.Root.GetProperty("operationId").GetGuid() == Guid.Empty ||
            result.Root.TryGetProperty("runId", out var run) && run.GetGuid() == Guid.Empty ||
            result.Root.TryGetProperty("attemptId", out var attempt) && attempt.GetGuid() == Guid.Empty ||
            result.PlanPath != $"blobs/plans/{result.PlanDigest}.json")
            throw new JsonException("journal_identity_invalid");
        ValidateUniqueIds(result.Root, "participants", "participantId");
        ValidateUniqueIds(result.Root, "receipts", "receiptId");
        var resources = result.Root.GetProperty("resourceKeys").EnumerateArray().Select(item => item.GetString()).ToHashSet();
        if (result.Root.GetProperty("participants").EnumerateArray().Any(item => !resources.Contains(item.GetProperty("resourceKey").GetString())))
            throw new JsonException("journal_participant_resource_unbound");
        Remember(key, result);
        return result;
    }

    private static void Remember(TextKey key, JournalDocument result)
    {
        if (result._bytes.Length > MaximumMemoOriginalBytes) return;
        lock (MemoGate)
        {
            if (Memo.ContainsKey(key)) return;
            while (Memo.Count >= MaximumMemoDocuments || _memoOriginalBytes + result._bytes.Length > MaximumMemoOriginalBytes)
            {
                var oldest = MemoOrder.Dequeue();
                _memoOriginalBytes -= Memo[oldest]._bytes.Length;
                Memo.Remove(oldest);
            }
            Memo.Add(key, result); MemoOrder.Enqueue(key);
            _memoOriginalBytes += result._bytes.Length;
        }
    }

    internal static void ValidateSuccessor(JournalDocument before, JournalDocument after)
    {
        var key = new SuccessorKey(before._textKey, after._textKey);
        lock (SuccessorGate) if (Successors.Contains(key)) return;
        ValidateSuccessorText(before, after);
        // This proves only the relation between immutable texts; effect admission remains outside this method.
        lock (SuccessorGate)
        {
            if (Successors.Contains(key)) return;
            if (Successors.Count == MaximumSuccessorProofs) Successors.Remove(SuccessorOrder.Dequeue());
            Successors.Add(key); SuccessorOrder.Enqueue(key);
        }
    }

    private static void ValidateSuccessorText(JournalDocument before, JournalDocument after)
    {
        foreach (var property in before.Root.EnumerateObject())
        {
            if (property.Name is "phase" or "revision" or "updatedAt" or "participants" or "receipts") continue;
            if (!after.Root.TryGetProperty(property.Name, out var next) || !JsonElement.DeepEquals(property.Value, next))
                throw new IOException("journal_frozen_identity_changed");
        }
        if (before.Root.EnumerateObject().Count() != after.Root.EnumerateObject().Count() || after.Revision != checked(before.Revision + 1))
            throw new IOException("journal_revision_conflict");
        ValidateAppendOnly(before.Root.GetProperty("participants"), after.Root.GetProperty("participants"));
        ValidateAppendOnly(before.Root.GetProperty("receipts"), after.Root.GetProperty("receipts"));
        if (before.Phase == after.Phase && JsonElement.DeepEquals(before.Root.GetProperty("participants"), after.Root.GetProperty("participants")) &&
            JsonElement.DeepEquals(before.Root.GetProperty("receipts"), after.Root.GetProperty("receipts")))
            throw new IOException("journal_empty_revision");
        if (before.Phase == JournalPhase.Committed) throw new IOException("journal_already_committed");
        if (before.Phase != after.Phase && JournalTransitionPolicy.Requirement(before.Kind, before.Phase, after.Phase) == TransitionRequirement.Forbidden)
            throw new IOException("journal_transition_rejected");
        if (before.Phase == JournalPhase.Prepared && after.Phase == JournalPhase.Committed &&
            !after.Root.GetProperty("receipts").EnumerateArray().Any(receipt => receipt.GetProperty("effect").GetString() == "notExecuted"))
            throw new IOException("journal_no_effect_receipt_required");
    }

    private static void ValidateAppendOnly(JsonElement before, JsonElement after)
    {
        if (after.GetArrayLength() < before.GetArrayLength()) throw new IOException("journal_evidence_removed");
        for (var index = 0; index < before.GetArrayLength(); index++)
            if (!JsonElement.DeepEquals(before[index], after[index])) throw new IOException("journal_evidence_changed");
    }

    private static void ValidateUniqueIds(JsonElement root, string collection, string id)
    {
        var values = root.GetProperty(collection).EnumerateArray().Select(item => item.GetProperty(id).GetGuid()).ToArray();
        if (values.Contains(Guid.Empty) || values.Distinct().Count() != values.Length)
            throw new JsonException("journal_evidence_identity_invalid");
    }

    private static T ParseEnum<T>(JsonElement root, string property) where T : struct, Enum =>
        Enum.Parse<T>(root.GetProperty(property).GetString()!, ignoreCase: true);
}
