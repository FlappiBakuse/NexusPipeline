using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Records;

internal enum RetirementContentKind { Logs, Images }

internal sealed record RecordRetirementArtifact(StepEvidenceBinding Binding, string Role, BlobReference Artifact,
    ImmutableArray<BlobReference> RawOutputs, ImmutableArray<BlobReference> RetainedContents);
internal sealed record RecordRetirementWriter(StepEvidenceBinding Binding, string Role, BlobReference Closure, ProcessIdentity? Root);
internal sealed record RecordRetirementAttempt(AttemptReceipt Receipt, int Revision, string JournalSha256, string HistorySha256,
    ImmutableArray<RecordRetirementArtifact> Artifacts, ImmutableArray<RecordRetirementWriter> Writers,
    ImmutableArray<RecordRetirementImage> Images);
internal sealed record RecordRetirementFamily(string Format, int Version, Guid InstanceId, Guid RunId,
    ImmutableArray<RecordRetirementAttempt> Attempts);
internal sealed record RecordRetirementDocument(string Format, int Version, Guid InstanceId, Guid OperationId,
    string SubjectId, Guid RunId, string AfterSha256, int Limit, BlobReference Family, Guid CleanupJournalId,
    BlobReference Cleanup, ImmutableArray<BlobReference> Contents, RetirementContentKind ContentKind);

internal static class RecordRetirementCodec
{
    internal const string FamilyFormat = "nexus.records.retirement-family";
    internal const string Format = "nexus.records.content-retirement";
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static BlobReference Write<T>(ImmutableBlobStore blobs, T value) => blobs.PersistPlan(BoundedJson.Serialize(value, Options));

    internal static T Read<T>(ImmutableBlobStore blobs, BlobReference reference)
    {
        StepEvidenceCodec.Plan(reference);
        using var input = blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var json = StrictJson.Parse(bytes);
        var value = json.RootElement.Deserialize<T>(Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(json.RootElement, JsonSerializer.SerializeToElement(value, Options))) throw Invalid();
        return value;
    }

    internal static void Validate(RecordRetirementFamily family, Guid instanceId)
    {
        if (family.Format != FamilyFormat || family.Version != 1 || family.InstanceId != instanceId || family.RunId == Guid.Empty ||
            family.Attempts.IsDefaultOrEmpty || family.Attempts.Length > 99) throw Invalid();
        for (var index = 0; index < family.Attempts.Length; index++)
        {
            var attempt = family.Attempts[index];
            if (attempt is null || attempt.Receipt is null || attempt.Receipt.Facts is null || attempt.Receipt.Facts.RunId != family.RunId ||
                attempt.Receipt.Facts.Ordinal != index + 1 || attempt.Revision is < 1 or > 4095 || !Digest(attempt.JournalSha256) || !Digest(attempt.HistorySha256) ||
                attempt.Artifacts.IsDefault || attempt.Artifacts.Length > 4096 || attempt.Writers.IsDefault || attempt.Writers.Length > 1024 ||
                attempt.Images.IsDefault || attempt.Images.Length > 4096) throw Invalid();
            RetirementImageEvidence.Validate(attempt);
            foreach (var writer in attempt.Writers)
            {
                if (writer is null || writer.Binding is null || writer.Binding.InstanceId != instanceId || writer.Binding.ParentJournalId != attempt.Receipt.JournalId ||
                    writer.Binding.AttemptId != attempt.Receipt.Facts.AttemptId || writer.Binding.RunId != family.RunId ||
                    writer.Binding.ParentOperationId != attempt.Receipt.OperationId ||
                    writer.Role is not ("process" or "judgement" or "preHook" or "postHook")) throw Invalid();
                StepEvidenceCodec.Plan(writer.Closure);
            }
            if (attempt.Writers.Select(item => item.Binding.JobName).Distinct(StringComparer.Ordinal).Count() != attempt.Writers.Length) throw Invalid();
            foreach (var artifact in attempt.Artifacts)
            {
                if (artifact is null || artifact.Binding is null || artifact.Binding.InstanceId != instanceId ||
                    artifact.Binding.RunId != family.RunId || artifact.Binding.AttemptId != attempt.Receipt.Facts.AttemptId ||
                    artifact.Binding.ParentJournalId != attempt.Receipt.JournalId ||
                    artifact.Binding.ParentOperationId != attempt.Receipt.OperationId || artifact.Role is not ("process" or "preHook" or "postHook") ||
                    artifact.RawOutputs.IsDefault || artifact.RawOutputs.Length > 8192 || artifact.RetainedContents.IsDefault || artifact.RetainedContents.Length > 32768) throw Invalid();
                StepEvidenceCodec.Plan(artifact.Artifact);
                foreach (var output in artifact.RawOutputs) StepEvidenceCodec.Plan(output);
                foreach (var content in artifact.RetainedContents)
                    if (content is null || !Digest(content.Sha256) || content.SizeBytes < 0 ||
                        content.RelativePath != $"blobs/content/{content.Sha256}.bin") throw Invalid();
                if (artifact.RawOutputs.Distinct().Count() != artifact.RawOutputs.Length) throw Invalid();
            }
            if (attempt.Artifacts.Select(item => (item.Binding, item.Role, item.Artifact)).Distinct().Count() != attempt.Artifacts.Length) throw Invalid();
        }
    }

    internal static void Validate(RecordRetirementDocument document, Guid instanceId)
    {
        if (document.Format != Format || document.Version != 1 || document.InstanceId != instanceId || document.OperationId == Guid.Empty ||
            document.RunId == Guid.Empty || string.IsNullOrWhiteSpace(document.SubjectId) || document.SubjectId.Length > 256 ||
            document.SubjectId.Any(char.IsControl) || document.AfterSha256 != "" && !Digest(document.AfterSha256) ||
            document.Limit is < 1 or > 128 || document.CleanupJournalId == Guid.Empty || document.Family is null || document.Cleanup is null ||
            document.Contents.IsDefaultOrEmpty || document.Contents.Length > document.Limit || !Enum.IsDefined(document.ContentKind)) throw Invalid();
        StepEvidenceCodec.Plan(document.Family); StepEvidenceCodec.Plan(document.Cleanup);
        var previous = document.AfterSha256;
        foreach (var content in document.Contents)
        {
            if (content is null || !Digest(content.Sha256) || content.SizeBytes < 0 || content.SizeBytes > (document.ContentKind == RetirementContentKind.Images ?
                    ExecutionCaptureInputs.MaximumPngBytes : 1048576) || document.ContentKind == RetirementContentKind.Images && content.SizeBytes < 57 ||
                content.RelativePath != $"blobs/content/{content.Sha256}.bin" || StringComparer.Ordinal.Compare(previous, content.Sha256) >= 0)
                throw Invalid();
            previous = content.Sha256;
        }
    }

    internal static bool Digest(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static IOException Invalid() => new("record_retirement_unbound");
}
