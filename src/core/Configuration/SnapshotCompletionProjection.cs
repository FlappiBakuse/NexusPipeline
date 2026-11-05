using System.Collections.Immutable;
using NexusPipeline.Core.Maintenance;

namespace NexusPipeline.Core.Configuration;

public sealed record SnapshotCompletionProjection(Guid ParentOperationId, Guid ParentJournalId, long ParentRevision,
    string ParentPlanDigest, Guid SessionId, Guid CompletionOperationId, string SubjectId, ConfigurationRunPhase Terminal,
    long GroupRevision, string EvidenceDigest, ImmutableArray<string> SlotKeys)
{
    internal static ImmutableArray<SnapshotCompletionProjection> FreezeAll(
        IReadOnlyList<SnapshotCompletionProjection> pending, RecoveryRegistrySnapshot registry)
    {
        if (pending is null || pending.Count > 4096) throw Invalid();
        var result = ImmutableArray.CreateBuilder<SnapshotCompletionProjection>();
        var sessions = new HashSet<Guid>(); var operations = new HashSet<Guid>();
        foreach (var item in pending)
        {
            if (item is null || item.ParentOperationId == Guid.Empty || item.ParentJournalId == Guid.Empty ||
                item.SessionId == Guid.Empty || item.CompletionOperationId == Guid.Empty ||
                item.ParentOperationId == item.SessionId || item.ParentOperationId == item.CompletionOperationId ||
                item.SessionId == item.CompletionOperationId || !sessions.Add(item.SessionId) || !operations.Add(item.CompletionOperationId) ||
                item.ParentRevision is < 1 or > 9007199254740991 || item.GroupRevision is < 1 or > 9007199254740991 ||
                !FileTreePolicy.Digest(item.ParentPlanDigest) || !FileTreePolicy.Digest(item.EvidenceDigest) ||
                string.IsNullOrWhiteSpace(item.SubjectId) || item.SubjectId.Length > 256 || item.SubjectId.Any(char.IsControl) ||
                item.Terminal is not (ConfigurationRunPhase.Completed or ConfigurationRunPhase.Interrupted) ||
                item.SlotKeys.IsDefaultOrEmpty || item.SlotKeys.Length > 4096 || item.SlotKeys.Any(key => !IsSlot(key)) ||
                item.SlotKeys.Distinct(StringComparer.Ordinal).Count() != item.SlotKeys.Length) throw Invalid();
            var parent = registry.Journals.SingleOrDefault(parent => parent.JournalId == item.ParentJournalId);
            if (parent is null || parent.Kind != JournalKind.RunAttempt || parent.OperationId != item.ParentOperationId ||
                parent.Revision != item.ParentRevision || parent.PlanDigest != item.ParentPlanDigest ||
                parent.Phase is JournalPhase.Committed or JournalPhase.Quarantined || item.SlotKeys.Any(key =>
                    !parent.ResourceKeys.Contains(key, StringComparer.Ordinal) || !registry.Isolations.Any(isolation =>
                        isolation.JournalId == item.ParentJournalId && isolation.ResourceKey == key && isolation.Mode == ResourceClaimMode.Mutation)))
                throw Invalid();
            result.Add(item with { SlotKeys = item.SlotKeys.Order(StringComparer.Ordinal).ToImmutableArray() });
        }
        return result.ToImmutable();
    }

    private static bool IsSlot(string key)
    {
        if (key is null || !key.StartsWith("configuration:", StringComparison.Ordinal) || key.Length < 52 ||
            key[50] != ':' || !Guid.TryParseExact(key.AsSpan(14, 36), "D", out var binding) || binding == Guid.Empty ||
            key.Substring(14, 36) != binding.ToString("D")) return false;
        try { _ = new ConfigurationAddress(binding, key[51..]); return key == key.Trim(); }
        catch (ConfigurationException) { return false; }
    }

    private static InvalidOperationException Invalid() => new("snapshot_completion_evidence_unbound");
}
