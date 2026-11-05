using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record AndroidControlBinding(Guid InstanceId, Guid ParentJournalId, Guid ParentOperationId,
    Guid AttemptId, string StepId, string TotalPlanSha256);
internal sealed record AndroidCollectedCommand(ExecutionTargetBinding Binding, BlobReference Collection);
internal sealed record AndroidCollectedDocument(string Format, int Version, AndroidControlBinding Binding,
    ImmutableArray<AndroidCollectedCommand> Commands, BlobReference Startup, TargetCleanupFact Cleanup);
internal sealed record AndroidCollectedIntent(string Format, int Version, AndroidControlBinding Binding,
    BlobReference Artifact, ImmutableArray<TargetCommandFact> Commands, TargetCleanupFact Cleanup);

internal static class AndroidCollectedCodec
{
    internal const string Format = "nexus.execution.android-collected";

    internal static bool IsParticipant(JsonElement participant) =>
        participant.GetProperty("identity").TryGetProperty("format", out var value) && value.GetString() == Format;

    internal static ImmutableArray<AndroidCollectedIntent> Read(JsonElement journal, ExecutionPlan total, string totalSha256)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var result = ImmutableArray.CreateBuilder<AndroidCollectedIntent>();
        foreach (var participant in journal.GetProperty("participants").EnumerateArray().Where(IsParticipant))
        {
            var identity = participant.GetProperty("identity");
            var decoded = ExecutionParticipantText.Read<AndroidCollectedIntent>(identity, StepResultCodec.Options, Invalid);
            var item = decoded.Value;
            var binding = item.Binding ?? throw Invalid();
            AndroidStartupCodec.RequireBinding(journal, total, totalSha256, participant, binding);
            if (!decoded.MatchesOriginal ||
                item.Format != Format || item.Version != 1 || participant.GetProperty("kind").GetString() != "process" ||
                binding.InstanceId.ToString("D") != total.InstanceId || binding.ParentJournalId != journal.GetProperty("journalId").GetGuid() ||
                binding.ParentOperationId != journal.GetProperty("operationId").GetGuid() || binding.AttemptId != journal.GetProperty("attemptId").GetGuid() ||
                binding.TotalPlanSha256 != totalSha256 || result.Any(value => value.Binding.StepId == binding.StepId))
                throw Invalid();
            var step = total.Steps.SingleOrDefault(step => step.StepId == binding.StepId) ?? throw Invalid();
            if (step.Target?.Kind != "android" || participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            var registrations = AndroidControlCodec.Read(journal, total, totalSha256)
                .Where(value => value.Binding.StepId == binding.StepId).ToArray();
            var startup = AndroidStartupCodec.Read(journal, total, totalSha256)
                .SingleOrDefault(value => value.Binding == binding) ?? throw Invalid();
            if (item.Cleanup is null || item.Commands.IsDefault || item.Commands.Length != registrations.Length ||
                !item.Commands.Select(command => command.CommandId).SequenceEqual(registrations.Select(command => command.Binding.TargetId)) ||
                !item.Commands.Take(startup.Commands.Length).SequenceEqual(startup.Commands)) throw Invalid();
            AndroidStartupCodec.RequireCommandFacts(binding.StepId, item.Commands, item.Cleanup);
            StepEvidenceCodec.Plan(item.Artifact); result.Add(item);
        }
        return result.ToImmutable();
    }

    internal static IReadOnlyList<BlobReference> References(AttemptWriterState state, AndroidControlEvidence reader,
        AndroidCollectedIntent intent, bool verifyContent) =>
        References(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, reader, intent, verifyContent);

    internal static IReadOnlyList<BlobReference> References(JsonElement journal, ExecutionPlan total, string totalSha256,
        AndroidControlEvidence reader, AndroidCollectedIntent intent, bool verifyContent)
    {
        var document = reader.Read<AndroidCollectedDocument>(intent.Artifact);
        var registrations = AndroidControlCodec.Read(journal, total, totalSha256)
            .Where(item => item.Binding.StepId == intent.Binding.StepId).ToArray();
        var startup = AndroidStartupCodec.Read(journal, total, totalSha256).Single(item => item.Binding == intent.Binding);
        if (document.Format != Format || document.Version != 1 || document.Binding != intent.Binding || document.Commands.IsDefault ||
            document.Startup != startup.Artifact || document.Cleanup is null ||
            !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(document.Cleanup, StepResultCodec.Options),
                JsonSerializer.SerializeToElement(intent.Cleanup, StepResultCodec.Options)) ||
            document.Commands.Length != registrations.Length || !document.Commands.Select(item => item.Binding).SequenceEqual(registrations.Select(item => item.Binding)))
            throw Invalid();
        var references = new List<BlobReference> { intent.Artifact, document.Startup }; long bytes = 0;
        var identities = new HashSet<(int ProcessId, DateTime Created)>();
        for (var index = 0; index < registrations.Length; index++)
        {
            var registration = registrations[index];
            var collection = reader.Collection(total, registration, document.Commands[index].Collection, verifyContent);
            var observation = reader.Observation(total, registration, collection.Observation, verifyContent: false);
            references.Add(document.Commands[index].Collection); references.Add(collection.Observation);
            references.AddRange(observation.Parts.Select(part => part.Content));
            references.AddRange(AndroidRetentionEvidence.Require(reader, total, registration, collection));
            if (collection.Retention is { } retainedReference)
            {
                var retained = reader.Read<AndroidLaunchRetained>(retainedReference);
                if (AndroidRetentionEvidence.Last(journal, registration.Binding, "retained") != retainedReference ||
                    AndroidRetentionEvidence.Last(journal, registration.Binding, "prepared") != retained.Intent) throw Invalid();
            }
            bytes = checked(bytes + observation.Parts.Sum(part => part.Content.SizeBytes));
            foreach (var member in collection.Inventory.AllFamily) identities.Add((member.ProcessId, member.CreatedAtUtc));
            if (index + 1 < registrations.Length && registrations[index + 1].PreviousObservation != collection.Observation) throw Invalid();
        }
        if (bytes > AndroidControlCapacity.MaximumBindingRawBytes || identities.Count > AndroidControlCapacity.MaximumObservedProcesses)
            throw Invalid();
        return references.Distinct().ToArray();
    }

    internal static JsonObject Participant(AndroidCollectedIntent item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, StepResultCodec.Options)
    };

    internal static void RequireResolved(JsonElement journal, ExecutionPlan total, string totalSha256, string? stepId = null)
    {
        var registrations = AndroidControlCodec.Read(journal, total, totalSha256);
        var startups = AndroidStartupCodec.Read(journal, total, totalSha256);
        var collected = Read(journal, total, totalSha256);
        var required = registrations.Select(item => item.Binding.StepId).Concat(startups.Select(item => item.Binding.StepId))
            .Where(value => stepId is null || value == stepId).Distinct(StringComparer.Ordinal);
        if (required.Any(value => !collected.Any(item => item.Binding.StepId == value)) ||
            collected.Where(item => stepId is null || item.Binding.StepId == stepId).Any(item => item.Commands.Any(command =>
                command.Kind == TargetCommandKind.Effect && command.Engine != EngineOutcome.NotStarted &&
                (command.Engine == EngineOutcome.Unknown && command.Observation != TargetCommandObservation.OwnedHandoff || command.Observation is TargetCommandObservation.Unverified or
                    TargetCommandObservation.Incomplete or TargetCommandObservation.Contradictory))))
            throw new IOException("execution_target_collection_pending");
    }

    private static JsonException Invalid() => new("execution_android_collection_unrecognized");
}
