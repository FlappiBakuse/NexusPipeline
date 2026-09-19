namespace NexusPipeline.Architecture;

public sealed record DebtComparison(
    IReadOnlyList<ArchitectureViolation> Added,
    IReadOnlyList<ArchitectureViolation> Removed,
    IReadOnlyList<ArchitectureViolation> Retained)
{
    public bool IsAllowed => Added.Count == 0;
}

public static class DebtBaseline
{
    public static DebtComparison Compare(
        IReadOnlyList<ArchitectureViolation> current,
        IReadOnlyList<ArchitectureViolation> approved)
    {
        var currentGroups = current.GroupBy(Key).ToDictionary(group => group.Key, group => group.ToList());
        var approvedGroups = approved.GroupBy(Key).ToDictionary(group => group.Key, group => group.ToList());
        var added = new List<ArchitectureViolation>();
        var removed = new List<ArchitectureViolation>();
        var retained = new List<ArchitectureViolation>();

        foreach (var (key, group) in currentGroups)
        {
            var allowed = approvedGroups.TryGetValue(key, out var baseline) ? baseline.Count : 0;
            retained.AddRange(group.Take(allowed));
            added.AddRange(group.Skip(allowed));
        }
        foreach (var (key, group) in approvedGroups)
        {
            var present = currentGroups.TryGetValue(key, out var currentGroup) ? currentGroup.Count : 0;
            removed.AddRange(group.Skip(present));
        }

        return new DebtComparison(added, removed, retained);
    }

    private static string Key(ArchitectureViolation violation) => string.Join('|',
        violation.RuleId,
        violation.StableFileId,
        violation.OriginalSymbolId,
        violation.TargetSymbolId,
        violation.NormalizedSyntaxHash,
        violation.OccurrenceOrdinal);
}
