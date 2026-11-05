namespace NexusPipeline.Platform.Storage.Journals;

internal static class JournalCapacityLimits
{
    internal const int MaximumRevisions = 4096;
    internal const long MaximumHistoryBytes = 67_108_864;
    internal const int MaximumParticipants = 4096;
    internal const int MaximumReceipts = 4096;
    internal const int MaximumOwnedJobs = 1024;
    internal const int MaximumWriterJobs = MaximumOwnedJobs;
}
