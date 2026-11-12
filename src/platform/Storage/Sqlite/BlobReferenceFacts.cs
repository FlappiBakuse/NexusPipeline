using NexusPipeline.Core.Configuration;
using System.Security.Cryptography;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed class BlobReferenceFacts
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _owners = [];
    private readonly Dictionary<BlobReference, (BlobReference Value, int Users)> _references = [];
    private readonly LinkedList<Guid> _order = [];
    private long _bytes;
    private sealed record Owner(Guid OperationId, JournalKind Kind, JournalPhase Phase, long Revision,
        string PlanPath, string PlanDigest, string ResourceDigest);
    private sealed record Entry(Owner Key, BlobReference[] References, LinkedListNode<Guid> Node, long Bytes);

    internal IReadOnlyList<BlobReference>? Find(RegisteredJournal journal)
    {
        lock (_gate)
        {
            if (!_owners.TryGetValue(journal.JournalId, out var saved)) return null;
            if (saved.Key != Key(journal)) { Remove(journal.JournalId); return null; }
            _order.Remove(saved.Node); _order.AddLast(saved.Node);
            return saved.References;
        }
    }

    internal void Store(RegisteredJournal journal, IReadOnlyList<BlobReference> references)
    {
        var values = references.Where(item => item.RelativePath.StartsWith("blobs/content/", StringComparison.Ordinal)).Distinct().ToArray();
        var owner = Key(journal);
        var ownerBytes = 512L + 2L * (owner.PlanPath.Length + owner.PlanDigest.Length + owner.ResourceDigest.Length) + values.Length * 8L;
        var maximum = ownerBytes + values.Sum(Size);
        if (maximum > 33554432 || values.Length > 65536) return;
        lock (_gate)
        {
            Remove(journal.JournalId);
            while (_owners.Count >= 8192 || _references.Count + values.Count(item => !_references.ContainsKey(item)) > 65536 ||
                _bytes + ownerBytes + values.Where(item => !_references.ContainsKey(item)).Sum(Size) > 33554432)
                Remove(_order.First!.Value);
            for (var index = 0; index < values.Length; index++)
            {
                var value = values[index];
                if (_references.TryGetValue(value, out var prior))
                { values[index] = prior.Value; _references[value] = (prior.Value, prior.Users + 1); }
                else { _references.Add(value, (value, 1)); _bytes += Size(value); }
            }
            _bytes += ownerBytes;
            _owners.Add(journal.JournalId, new(owner, values, _order.AddLast(journal.JournalId), ownerBytes));
        }
    }

    private void Remove(Guid journal)
    {
        if (!_owners.Remove(journal, out var saved)) return;
        _order.Remove(saved.Node); _bytes -= saved.Bytes;
        foreach (var value in saved.References)
        {
            var prior = _references[value];
            if (prior.Users == 1) { _references.Remove(value); _bytes -= Size(value); }
            else _references[value] = (prior.Value, prior.Users - 1);
        }
    }

    private static long Size(BlobReference value) => 96L + 2L * (value.RelativePath.Length + value.Sha256.Length);
    private static Owner Key(RegisteredJournal journal) => new(journal.OperationId, journal.Kind, journal.Phase,
        journal.Revision, journal.PlanPath, journal.PlanDigest,
        Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(journal.ResourceKeys))));
}
