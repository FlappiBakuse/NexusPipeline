using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record CaptureOriginalAppend(ExecutionCaptureIntent Intent, JournalDocument Before, JournalDocument After);

internal static class CaptureOriginalPrefix
{
    internal static ImmutableArray<CaptureOriginalAppend> Read(IReadOnlyList<JournalDocument> history,
        ExecutionPlan total, string totalSha256)
    {
        if (history.Count is 0 or > JournalCapacityLimits.MaximumRevisions || history[0].Revision != 1) throw Invalid();
        var result = ImmutableArray.CreateBuilder<CaptureOriginalAppend>();
        for (var index = 0; index < history.Count; index++)
        {
            var after = history[index];
            _ = ExecutionCaptureCodec.Read(after.Root, total, totalSha256);
            if (index == 0)
            {
                if (after.Root.GetProperty("participants").EnumerateArray().Any(ExecutionCaptureCodec.IsParticipant)) throw Invalid();
                continue;
            }
            var before = history[index - 1];
            JournalDocument.ValidateSuccessor(before, after);
            var oldItems = before.Root.GetProperty("participants");
            var newItems = after.Root.GetProperty("participants");
            foreach (var participant in newItems.EnumerateArray().Skip(oldItems.GetArrayLength()).Where(ExecutionCaptureCodec.IsParticipant))
            {
                var intent = RequireAppend(before, after, participant);
                result.Add(new(intent, before, after));
            }
        }
        return result.ToImmutable();
    }

    internal static ExecutionCaptureIntent RequireAppend(JournalDocument before, JournalDocument after, JsonElement participant)
    {
        JournalDocument.ValidateSuccessor(before, after);
        var oldItems = before.Root.GetProperty("participants");
        var newItems = after.Root.GetProperty("participants");
        var intent = ExecutionCaptureCodec.Decode(participant);
        if (intent.BeforeRevision != before.Revision || after.Revision != before.Revision + 1 ||
            newItems.GetArrayLength() != oldItems.GetArrayLength() + 1 ||
            !JsonElement.DeepEquals(newItems[newItems.GetArrayLength() - 1], participant) ||
            before.Kind != JournalKind.RunAttempt || after.Kind != JournalKind.RunAttempt ||
            !JsonElement.DeepEquals(before.Root.GetProperty("receipts"), after.Root.GetProperty("receipts")) ||
            before.Phase != after.Phase || after.Phase is not (JournalPhase.Applying or JournalPhase.Applied or JournalPhase.Quarantined))
            throw Invalid();
        if ((intent.Stage is "source" or "root" || MuMuCaptureSequence.IsRoot(intent.Stage)) &&
            before.Phase != JournalPhase.Applying) throw Invalid();
        return intent;
    }

    internal static IOException Invalid() => new("execution_capture_original_prefix_unbound");
}
