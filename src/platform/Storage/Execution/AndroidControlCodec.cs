using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class AndroidControlCodec
{
    internal const string RegistrationFormat = "nexus.execution.android-command";
    internal const string ObservationFormat = "nexus.execution.android-command-observation";
    internal const string CollectionFormat = "nexus.execution.android-command-collection";
    internal const string ControlPlanFormat = "nexus.execution.android-control-plan";

    internal static bool IsParticipant(JsonElement participant) =>
        participant.GetProperty("identity").TryGetProperty("format", out var value) && value.GetString() == RegistrationFormat;

    internal static ImmutableArray<AndroidCommandRegistration> Read(JsonElement journal, ExecutionPlan total, string totalSha256)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var result = ImmutableArray.CreateBuilder<AndroidCommandRegistration>();
        var ids = new HashSet<Guid>(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var participant in journal.GetProperty("participants").EnumerateArray().Where(IsParticipant))
        {
            var identity = participant.GetProperty("identity");
            var decoded = ExecutionParticipantText.Read<AndroidCommandRegistration>(identity, StepResultCodec.Options, Invalid);
            var item = decoded.Value;
            if (!decoded.MatchesOriginal ||
                item.Format != RegistrationFormat || item.Version != 1 || item.Binding is null) throw Invalid();
            var binding = item.Binding;
            var step = total.Steps.SingleOrDefault(step => step.StepId == binding.StepId) ?? throw Invalid();
            var prior = result.Where(value => value.Binding.StepId == binding.StepId).ToArray();
            var limits = AndroidControlCapacity.Derive(step);
            if (step.Target is not { Kind: "android", Android: not null, Pc: null } ||
                participant.GetProperty("kind").GetString() != "process" ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId ||
                binding.InstanceId.ToString("D") != total.InstanceId ||
                binding.ParentJournalId != journal.GetProperty("journalId").GetGuid() ||
                binding.ParentOperationId != journal.GetProperty("operationId").GetGuid() ||
                binding.AttemptId != journal.GetProperty("attemptId").GetGuid() ||
                binding.TotalPlanSha256 != totalSha256 || binding.TargetId == Guid.Empty ||
                binding.OwnerWriterId == Guid.Empty || binding.SessionId < 0 ||
                !ids.Add(binding.TargetId) || !names.Add(binding.JobName) || !JobName(binding.JobName) ||
                item.Sequence != prior.Length || item.Sequence >= limits.MaximumCommands ||
                !Enum.IsDefined(item.Purpose) || !Enum.IsDefined(item.Action) || !FileTreePolicy.Digest(item.RequestDigest) ||
                (prior.Length == 0) != (item.PreviousObservation is null) ||
                prior.Length > 0 && item.ControlPlan != prior[0].ControlPlan) throw Invalid();
            StepEvidenceCodec.Plan(item.Request); StepEvidenceCodec.Plan(item.ControlPlan);
            if (item.PreviousObservation is { } reference) StepEvidenceCodec.Plan(reference);
            result.Add(item);
        }
        return result.ToImmutable();
    }

    internal static JsonObject Participant(AndroidCommandRegistration item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process",
        ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, StepResultCodec.Options)
    };

    private readonly record struct DocumentKey(Type Type, JsonSerializerOptions Options, int Length, string Sha256);
    private sealed record DecodedDocument(byte[] Original, object Value);
    private const int MaximumDecodedDocuments = 128;
    private const int MaximumOriginalDocumentBytes = 1024 * 1024;
    private static readonly object DocumentGate = new();
    private static readonly Dictionary<DocumentKey, DecodedDocument> Documents = [];
    private static readonly Queue<DocumentKey> DocumentOrder = [];
    private static int _documentBytes;

    internal static T Decode<T>(ReadOnlyMemory<byte> bytes) where T : class
    {
        var type = typeof(T);
        if (type != typeof(AndroidCommandRequest) && type != typeof(AndroidControlPlan) &&
            type != typeof(AndroidCommandObservation) && type != typeof(AndroidCommandCollection) &&
            type != typeof(AndroidStartupDocument) && type != typeof(AndroidCollectedDocument) ||
            bytes.Length is < 1 or > NexusPipeline.Platform.Serialization.StrictJson.MaximumBytes)
            return DecodeOriginal<T>(bytes);
        var owned = bytes.ToArray();
        var key = new DocumentKey(type, StepResultCodec.Options, owned.Length,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(owned)));
        lock (DocumentGate)
            if (Documents.TryGetValue(key, out var prior) && prior.Original.AsSpan().SequenceEqual(owned)) return (T)prior.Value;
        var value = DecodeOriginal<T>(owned);
        if (value is AndroidCommandRequest { Tool: { RuntimeFiles: { } files } tool } request)
            value = (T)(object)(request with { Tool = tool with { RuntimeFiles = Array.AsReadOnly(files.ToArray()) } });
        Remember(key, owned, value);
        return value;
    }

    private static void Remember(DocumentKey key, byte[] owned, object value)
    {
        if (owned.Length > MaximumOriginalDocumentBytes) return;
        lock (DocumentGate)
        {
            if (Documents.ContainsKey(key)) return;
            while (Documents.Count >= MaximumDecodedDocuments || _documentBytes + owned.Length > MaximumOriginalDocumentBytes)
            {
                var oldest = DocumentOrder.Dequeue();
                _documentBytes -= Documents[oldest].Original.Length; Documents.Remove(oldest);
            }
            // Typed immutable decoding is reusable; current files, subjects, native roots and domain decisions are not.
            Documents.Add(key, new(owned, value)); DocumentOrder.Enqueue(key); _documentBytes += owned.Length;
        }
    }

    private static T DecodeOriginal<T>(ReadOnlyMemory<byte> bytes) where T : class
    {
        using var parsed = NexusPipeline.Platform.Serialization.StrictJson.Parse(bytes);
        var value = parsed.RootElement.Deserialize<T>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(value, StepResultCodec.Options)))
            throw Invalid();
        return value;
    }

    internal static bool JobName(string? name) => name is { Length: 50 } &&
        name.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) &&
        Guid.TryParseExact(name[18..], "N", out var id) && id != Guid.Empty && name[18..] == id.ToString("N");
    private static JsonException Invalid() => new("execution_android_control_unrecognized");
}
