using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ResultAdmissionItem(long Revision, bool HasLocalStop, BusinessOutcome Business, BusinessOutcome PreviousBusiness);

internal static class StepResultAdmission
{
    internal static bool SuppressesLocalStop(ExecutionCancellationIntent? close) => close is not null &&
        close.ReasonCode != DispatchIntentCodec.CompletionReason;

    internal static IReadOnlyList<ResultAdmissionItem> Read(RuntimeInstance instance, ContractReader contracts,
        JournalDocument projected, IReadOnlyList<JournalDocument> tail)
    {
        var result = new List<ResultAdmissionItem>();
        var blobs = new ImmutableBlobStore(instance, contracts);
        var projectedResults = StepResultCodec.ReadAll(projected.Root);
        var business = projectedResults.GroupBy(item => item.Binding.JobName).ToDictionary(group => group.Key, group => group.Last().Business);
        var previousCount = projectedResults.Count;
        foreach (var next in tail)
        {
            var current = StepResultCodec.ReadAll(next.Root);
            foreach (var added in current.Skip(previousCount))
            {
                var document = StepEvidenceCodec.Read(blobs, added.Binding, added.Artifact);
                result.Add(new(next.Revision, document.Stop is not null, document.Business,
                    business.GetValueOrDefault(added.Binding.JobName, BusinessOutcome.Unverified)));
                business[added.Binding.JobName] = document.Business;
            }
            previousCount = current.Count;
        }
        return result.AsReadOnly();
    }

    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction, AttemptWriterState state,
        ResultAdmissionItem item)
    {
        if (!SuppressesLocalStop(ExecutionCancellationRows.Read(connection, transaction, state.Journal, state.Attempt))) return;
        if (item.HasLocalStop) throw new IOException("execution_stop_admission_closed");
        if (item.Business != item.PreviousBusiness) throw new IOException("execution_judgement_admission_closed");
    }
}
