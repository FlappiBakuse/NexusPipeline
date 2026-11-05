using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class PreviewDomainProjection
{
    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument before, JournalDocument next, PreparedPreviewClosure? closure)
    {
        _ = PreviewDomainCodec.Read(next.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        var previous = before.Root.GetProperty("participants").EnumerateArray().Where(PreviewDomainCodec.IsParticipant).ToArray();
        var current = next.Root.GetProperty("participants").EnumerateArray().Where(PreviewDomainCodec.IsParticipant).ToArray();
        if (current.Length < previous.Length || previous.Where((value, index) =>
            !System.Text.Json.JsonElement.DeepEquals(value, current[index])).Any()) throw PreviewDomainCodec.Invalid();
        var added = current.Skip(previous.Length).ToArray();
        if (added.Length == 0) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (added.Length != 1 || !state.IsExecutionStart || state.Gate.State != EffectGateState.Entered ||
            before.Phase is not (JournalPhase.Applying or JournalPhase.Quarantined)) throw PreviewDomainCodec.Invalid();
        var item = PreviewDomainCodec.Decode(added[0]); var source = item.Source;
        if (item.BeforeRevision != before.Revision || next.Revision != before.Revision + 1) throw PreviewDomainCodec.Invalid();
        AttemptWriterState.RequireCursor(connection, transaction, state.Journal, EffectGateState.Entered);
        PreviewDomainEvidence.RequireSourceInside(connection, transaction, source);
        if (item.Stage == "open")
        {
            ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, source.Primary.ParentJournalId);
            var main = WriterIntentCodec.ReadJobs(before.Root, state.Total).Single(job =>
                job.StepId == source.Primary.StepId && job.Role == "process");
            if (before.Phase != JournalPhase.Applying || main.ClosedElapsedMilliseconds is not null) throw PreviewDomainCodec.Invalid();
            // Recovery may project a durable open only after the original producer has stopped.
            if (source.Creator != JournalWriterStore.CurrentWriter.Process || source.Primary.WriterId != JournalWriterStore.CurrentWriter.WriterId)
                PreviewDomainEvidence.RequireColdStopped(source);
        }
        else if (closure is not null) closure.Require(item);
        else PreviewDomainEvidence.RequireColdStopped(source);
    }
}
