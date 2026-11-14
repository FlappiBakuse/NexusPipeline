using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ActivationFailureFacts(Guid ActivationId, JournalWriter Writer);
internal sealed record ExtensionRestorationRecord(string Format, int Version, Guid InstanceId,
    Guid OperationId, Guid JournalId, BlobReference Plan, BlobReference Source, BlobReference Physical,
    long BeforeRevision, string Stage, bool RetireCandidate, bool RestoreOriginal,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActivationFailureFacts? Failure = null);
internal sealed record ExtensionRestorationIntent(string Format, int Version,
    long BeforeRevision, string Stage, BlobReference Artifact);
internal sealed record ExtensionRestorationEntry(ExtensionRestorationIntent Intent,
    ExtensionRestorationRecord Record);

internal sealed class ExtensionRestorationCodec(RuntimeInstance instance, ContractReader contracts)
{
    internal const string Format = "nexus.extension.payload-restoration";
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal BlobReference Persist(ExtensionRestorationRecord value, FrozenPayloadApply plan,
        BlobReference originalPlan, BlobReference physical)
    {
        Require(value, plan, originalPlan, physical);
        return _blobs.PersistPlan(BoundedJson.Serialize(value, Options));
    }

    internal ImmutableArray<ExtensionRestorationEntry> ReadHistory(IReadOnlyList<JournalDocument> history,
        FrozenPayloadApply plan, BlobReference originalPlan, ImmutableArray<ExtensionPhysicalEntry> physical)
    {
        var result = ImmutableArray.CreateBuilder<ExtensionRestorationEntry>();
        foreach (var document in history)
        {
            var intents = ReadIntents(document.Root, plan.ExtensionId!);
            if (intents.Length < result.Count || intents.Length > result.Count + 1) throw Invalid();
            for (var i = 0; i < result.Count; i++) if (intents[i] != result[i].Intent) throw Invalid();
            if (intents.Length == result.Count)
            {
                if (result.Count != 0) throw Invalid();
                continue;
            }
            if (physical.IsEmpty || document.Phase is not (JournalPhase.Reverting or JournalPhase.Reverted or JournalPhase.Committed))
                throw Invalid();
            var intent = intents[^1]; Reference(intent.Artifact);
            using var input = _blobs.OpenVerified(intent.Artifact);
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            using var strict = StrictJson.Parse(bytes);
            var value = JsonSerializer.Deserialize<ExtensionRestorationRecord>(bytes, Options) ?? throw Invalid();
            if (!JsonElement.DeepEquals(strict.RootElement, JsonSerializer.SerializeToElement(value, Options))) throw Invalid();
            Require(value, plan, originalPlan, physical[^1].Intent.Artifact);
            if ((physical.Length == ExtensionPhysicalCodec.Sequence(plan).Length) != (value.Failure is not null))
                throw Invalid();
            if (value.BeforeRevision != intent.BeforeRevision || value.BeforeRevision != document.Revision - 1 ||
                value.Stage != intent.Stage || result.Count == 0 && value.Stage != "started") throw Invalid();
            var first = result.Count == 0 ? value : result[0].Record;
            if (value.Failure != first.Failure || value.RetireCandidate != first.RetireCandidate || value.RestoreOriginal != first.RestoreOriginal ||
                result.Count >= Sequence(first).Length || value.Stage != Sequence(first)[result.Count] || document.Phase != Phase(value.Stage)) throw Invalid();
            result.Add(new(intent, value));
        }
        return result.ToImmutable();
    }

    internal static ImmutableArray<string> Sequence(ExtensionRestorationRecord first)
    {
        var result = ImmutableArray.CreateBuilder<string>(); result.Add("started");
        if (first.RetireCandidate) result.Add("candidate-retired");
        if (first.RestoreOriginal) result.Add("original-restored");
        result.Add("restored"); result.Add("committed"); return result.ToImmutable();
    }

    internal static JournalPhase Phase(string stage) => stage switch
    {
        "started" or "candidate-retired" or "original-restored" => JournalPhase.Reverting,
        "restored" => JournalPhase.Reverted, "committed" => JournalPhase.Committed, _ => throw Invalid()
    };

    internal static bool IsParticipant(JsonElement participant) =>
        participant.TryGetProperty("identity", out var identity) && identity.TryGetProperty("format", out var format) &&
        format.ValueKind == JsonValueKind.String && format.GetString() == Format;

    internal static JsonObject Participant(ExtensionRestorationIntent value, string extensionId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "directory",
        ["resourceKey"] = "extension:" + extensionId, ["identity"] = JsonSerializer.SerializeToNode(value, Options)
    };

    internal static ImmutableArray<ExtensionRestorationIntent> ReadIntents(JsonElement root, string extensionId)
    {
        var result = ImmutableArray.CreateBuilder<ExtensionRestorationIntent>();
        foreach (var participant in root.GetProperty("participants").EnumerateArray())
        {
            if (!IsParticipant(participant)) continue;
            var identity = participant.GetProperty("identity");
            var value = identity.Deserialize<ExtensionRestorationIntent>(Options) ?? throw Invalid();
            if (!JsonElement.DeepEquals(identity, JsonSerializer.SerializeToElement(value, Options)) ||
                value.Format != Format || value.Version != 1 || value.BeforeRevision < 1 ||
                !Guid.TryParseExact(participant.GetProperty("participantId").GetString(), "D", out var id) || id == Guid.Empty ||
                participant.GetProperty("kind").GetString() != "directory" ||
                participant.GetProperty("resourceKey").GetString() != "extension:" + extensionId ||
                value.Stage is not ("started" or "candidate-retired" or "original-restored" or "restored" or "committed"))
                throw Invalid();
            Reference(value.Artifact); result.Add(value); if (result.Count > 5) throw Invalid();
        }
        return result.ToImmutable();
    }

    internal static void RequireRevision(JournalDocument document, FrozenPayloadApply plan,
        ImmutableArray<ExtensionRestorationEntry> entries, int physicalCount)
    {
        var found = ReadIntents(document.Root, plan.ExtensionId!);
        if (found.IsEmpty || found.Length > entries.Length || document.Root.GetProperty("participants").GetArrayLength() != physicalCount + found.Length ||
            document.Phase != Phase(found[^1].Stage)) throw Invalid();
        if (!found.SequenceEqual(entries.Take(found.Length).Select(item => item.Intent))) throw Invalid();
        var receipts = document.Root.GetProperty("receipts").EnumerateArray().ToArray();
        if (document.Phase == JournalPhase.Committed)
        {
            if (receipts.Length != 1 || !Guid.TryParseExact(receipts[0].GetProperty("receiptId").GetString(), "D", out var id) ||
                id == Guid.Empty || receipts[0].GetProperty("stepId").GetString() != "extension-restoration" ||
                receipts[0].GetProperty("effect").GetString() != "reverted" ||
                receipts[0].GetProperty("detailCode").GetString() != "extension_original_restored" ||
                receipts[0].TryGetProperty("outcomes", out _)) throw Invalid();
        }
        else if (receipts.Length != 0) throw Invalid();
    }

    private void Require(ExtensionRestorationRecord value, FrozenPayloadApply plan, BlobReference originalPlan, BlobReference physical)
    {
        if (value is null || value.Format != Format || value.Version != 1 || value.InstanceId != instance.InstanceId ||
            value.InstanceId != plan.InstanceId || value.OperationId != plan.OperationId || value.JournalId != plan.JournalId ||
            plan.Kind != JournalKind.ExtensionApply || value.Plan != originalPlan || value.Source != plan.ExtensionSource ||
            value.Physical != physical || value.BeforeRevision < 1 ||
            value.Stage is not ("started" or "candidate-retired" or "original-restored" or "restored" or "committed") ||
            plan.Action == PayloadAction.Remove && value.RetireCandidate ||
            plan.Before.Snapshot.Shape == NexusPipeline.Core.Configuration.FileObjectKind.Missing && value.RestoreOriginal)
            throw Invalid();
        if (value.Failure is not null)
        {
            if (plan.Action == PayloadAction.Remove) throw Invalid();
            RequireFailure(value.Failure);
        }
        Reference(value.Plan); Reference(value.Source); Reference(value.Physical);
    }

    internal static void RequireFailureInside(SqliteConnection connection, SqliteTransaction transaction,
        Guid journalId, ActivationFailureFacts failure)
    {
        RequireFailure(failure);
        var writer = failure.Writer;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.journal_writers WHERE journal_id=$journal AND writer_id=$writer " +
            "AND process_id=$pid AND created_file_time=$created AND image_path=$path AND image_sha256=$hash " +
            "AND user_sid=$sid AND elevated=$elevated) AND NOT EXISTS(SELECT 1 FROM main.journal_writer_jobs WHERE journal_id=$journal);";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$writer", writer.WriterId.ToString("D"));
        command.Parameters.AddWithValue("$pid", writer.Process.ProcessId);
        command.Parameters.AddWithValue("$created", writer.Process.CreatedAtUtc.ToFileTimeUtc());
        command.Parameters.AddWithValue("$path", writer.Process.ImagePath);
        command.Parameters.AddWithValue("$hash", writer.Process.ImageSha256);
        command.Parameters.AddWithValue("$sid", writer.Process.UserSid);
        command.Parameters.AddWithValue("$elevated", writer.Process.IsElevated);
        if ((long)command.ExecuteScalar()! != 1) throw Invalid();
    }

    private static void RequireFailure(ActivationFailureFacts failure)
    {
        if (failure.ActivationId == Guid.Empty || failure.Writer is not { } writer || writer.WriterId == Guid.Empty ||
            writer.Process is not { } process || process.ProcessId <= 0 || process.CreatedAtUtc == default ||
            process.CreatedAtUtc.Kind != DateTimeKind.Utc || process.CreatedAtUtc < DateTime.FromFileTimeUtc(0) || !Path.IsPathFullyQualified(process.ImagePath) ||
            string.IsNullOrWhiteSpace(process.UserSid) || process.ImageSha256 is not { Length: 64 } ||
            process.ImageSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw Invalid();
    }

    private static void Reference(BlobReference value)
    {
        if (value is null || value.SizeBytes is < 1 or > 2097152 || value.Sha256 is not { Length: 64 } ||
            value.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            value.RelativePath != "blobs/plans/" + value.Sha256 + ".json") throw Invalid();
    }
    private static IOException Invalid() => new("extension_restoration_evidence_unbound");
}
