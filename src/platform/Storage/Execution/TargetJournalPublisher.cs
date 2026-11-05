using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class TargetJournalPublisher
{
    internal static BlobReference Publish<T>(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AttemptWriterState state, ExecutionTargetBinding binding, string stage, T document)
    {
        return PublishParticipant(instance, database, contracts, state, document, artifact =>
            TargetJobCodec.Participant(new(TargetJobCodec.Format, 1, binding, stage, artifact),
                state.Total.Steps.Single(step => step.StepId == binding.StepId).ScriptId));
    }

    internal static BlobReference PublishParticipant<T>(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AttemptWriterState state, T document, Func<BlobReference, JsonObject> participant)
    {
        var blobs = new ImmutableBlobStore(instance, contracts);
        var artifact = blobs.PersistPlan(BoundedJson.Serialize(document, StepResultCodec.Options));
        using var held = blobs.OpenVerified(artifact);
        AppendParticipant(instance, database, contracts, state, participant(artifact));
        return artifact;
    }

    internal static void Append(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AttemptWriterState state, TargetJobIntent item) =>
        AppendParticipant(instance, database, contracts, state, TargetJobCodec.Participant(item,
            state.Total.Steps.Single(step => step.StepId == item.Binding.StepId).ScriptId));

    internal static void AppendParticipant(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AttemptWriterState state, JsonObject participant)
    {
        var document = JsonNode.Parse(state.Latest.Export())!.AsObject();
        document["revision"] = state.Latest.Revision + 1;
        document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        document["participants"]!.AsArray().Add(participant);
        new JournalStore(instance, contracts).Append(state.Journal.JournalId, state.Latest.Revision, JsonSerializer.SerializeToUtf8Bytes(document));
        ExecutionAttemptProjection.Synchronize(instance, database, contracts,
            AttemptWriterState.Read(instance, database, contracts, state.Journal.JournalId));
    }
}
