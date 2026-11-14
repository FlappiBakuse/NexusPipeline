using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record PreviewDomainSource(StepEvidenceBinding Primary, string TotalPlanSha256,
    string JobName, ProcessIdentity Creator, ProcessIdentity MainRoot, uint Flags, uint ActiveLimit, ulong MemoryLimit);
internal sealed record PreviewDomainIntent(string Format, int Version, PreviewDomainSource Source,
    string Stage, long BeforeRevision, uint? TotalProcesses);
internal sealed record PreviewDomainState(PreviewDomainSource Source, bool Closed);

internal static class PreviewDomainCodec
{
    internal const string Format = "nexus.execution.preview-domain";

    internal static string Name(StepEvidenceBinding primary, string totalSha256) =>
        "Local\\NexusWorker-" + Convert.ToHexString(SHA256.HashData(BoundedJson.Serialize(
            new { primary, totalSha256 }, StepResultCodec.Options)))[..32].ToLowerInvariant();

    internal static bool IsParticipant(JsonElement participant) =>
        participant.GetProperty("identity").TryGetProperty("format", out var format) && format.GetString() == Format;

    internal static PreviewDomainIntent Decode(JsonElement participant)
    {
        var decoded = ExecutionParticipantText.Read<PreviewDomainIntent>(participant.GetProperty("identity"),
            StepResultCodec.Options, Invalid);
        var value = decoded.Value;
        if (!decoded.MatchesOriginal || value.Format != Format || value.Version != 1 || value.Source?.Primary is null ||
            value.Source.Creator is null || value.Source.MainRoot is null || value.Stage is not ("open" or "closed") ||
            value.Stage == "open" && value.TotalProcesses is not null ||
            value.Source.Flags != 0x2208 || value.Source.ActiveLimit != 64 || value.Source.MemoryLimit != 268435456 ||
            value.Source.JobName != Name(value.Source.Primary, value.Source.TotalPlanSha256) ||
            !ExecutionCaptureCodec.JobName(value.Source.JobName)) throw Invalid();
        return value;
    }

    internal static ImmutableArray<PreviewDomainState> Read(JsonElement journal, ExecutionPlan total, string totalSha256)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var states = new Dictionary<string, PreviewDomainState>(StringComparer.Ordinal);
        var jobs = WriterIntentCodec.ReadJobs(journal, total);
        long priorRevision = 0;
        foreach (var participant in journal.GetProperty("participants").EnumerateArray().Where(IsParticipant))
        {
            var item = Decode(participant); var source = item.Source; var primary = source.Primary;
            var step = total.Steps.SingleOrDefault(step => step.StepId == primary.StepId) ?? throw Invalid();
            var main = jobs.SingleOrDefault(job => job.StepId == primary.StepId && job.Role == "process") ?? throw Invalid();
            if (total.CaptureRuntime is null || !ExecutionCaptureInputs.IsSupported(step) ||
                source.TotalPlanSha256 != totalSha256 || StepResultCodec.Binding(journal, main) != primary ||
                item.BeforeRevision <= priorRevision || item.BeforeRevision >= journal.GetProperty("revision").GetInt64() ||
                participant.GetProperty("kind").GetString() != "process" ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            priorRevision = item.BeforeRevision;
            if (item.Stage == "open")
            {
                if (states.ContainsKey(primary.StepId) || states.Values.Any(value => value.Source.JobName == source.JobName)) throw Invalid();
                states.Add(primary.StepId, new(source, false));
            }
            else
            {
                if (!states.TryGetValue(primary.StepId, out var previous) || previous.Closed || previous.Source != source) throw Invalid();
                states[primary.StepId] = previous with { Closed = true };
            }
        }
        return total.Steps.Where(step => states.ContainsKey(step.StepId)).Select(step => states[step.StepId]).ToImmutableArray();
    }

    internal static void RequireHistory(IEnumerable<JournalDocument> history, ExecutionPlan total, string totalSha256,
        JournalDocument? previous = null)
    {
        JsonElement[] prior = previous is null ? [] : previous.Root.GetProperty("participants").EnumerateArray().Where(IsParticipant).ToArray();
        foreach (var document in history)
        {
            _ = Read(document.Root, total, totalSha256);
            var current = document.Root.GetProperty("participants").EnumerateArray().Where(IsParticipant).ToArray();
            if (current.Length < prior.Length || prior.Where((value, index) => !JsonElement.DeepEquals(value, current[index])).Any()) throw Invalid();
            foreach (var added in current.Skip(prior.Length))
                if (Decode(added).BeforeRevision != document.Revision - 1) throw Invalid();
            prior = current;
        }
    }

    internal static void RequireResolved(JsonElement journal, ExecutionPlan total, string totalSha256, string? stepId = null)
    {
        if (Read(journal, total, totalSha256).Any(value => !value.Closed && (stepId is null || value.Source.Primary.StepId == stepId)))
            throw new IOException("execution_preview_domain_collection_pending");
    }

    internal static JsonObject Participant(PreviewDomainIntent item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, StepResultCodec.Options)
    };

    internal static IOException Invalid() => new("execution_preview_domain_unbound");
}
