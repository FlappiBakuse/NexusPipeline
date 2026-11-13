namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed class BlobReferenceScan(IReadOnlySet<BlobReference> candidates)
{
    internal IReadOnlySet<BlobReference> Candidates { get; } = candidates.ToHashSet();
    internal HashSet<BlobReference> Referenced { get; } = [];
    internal string After { get; set; } = "";
    internal bool NotificationsRead { get; set; }
    internal bool Completed { get; set; }

    internal void RequireCandidates(IReadOnlySet<BlobReference> candidates)
    {
        if (!Candidates.IsSupersetOf(candidates)) throw new ArgumentException("blob_reference_scan_candidates_changed");
    }
}
