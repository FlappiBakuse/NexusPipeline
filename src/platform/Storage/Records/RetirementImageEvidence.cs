using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Records;

internal sealed record RecordRetirementImage(ExecutionCaptureBinding Binding, int Position, RunImageOutcome Outcome,
    string ReasonCode, int? Width, int? Height, BlobReference Source, BlobReference? Root, BlobReference Result,
    BlobReference? Png, BlobReference ParentClosure)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? FrameId { get; init; }

    internal ImageCatalogEntry ToEntry() =>
        new(new(Position, new(Binding.Primary.RunId, Binding.Primary.AttemptId, Binding.Primary.StepId, FrameId ?? Binding.CaptureId),
            Outcome, ReasonCode, Width, Height, Source.Sha256, Root?.Sha256, Result.Sha256, Png?.Sha256, Png?.SizeBytes),
            Source, Root, Result, Png);
}

internal static class RetirementImageEvidence
{
    internal static ImmutableArray<RecordRetirementImage> Read(ImageCatalogSnapshot snapshot,
        IReadOnlyList<CaptureSavedEvidence> saved)
    {
        return snapshot.Entries.Select(entry => new RecordRetirementImage(snapshot.Bindings[entry.Item.Key.CaptureId],
            entry.Item.Position, entry.Item.Outcome, entry.Item.ReasonCode, entry.Item.Width, entry.Item.Height,
            entry.Source, entry.Root, entry.Result, entry.Png,
            saved.Single(value => value.State.Result == entry.Result).Result!.ParentClosure)
        { FrameId = entry.Item.Key.CaptureId == snapshot.Bindings[entry.Item.Key.CaptureId].CaptureId
            ? null : entry.Item.Key.CaptureId }).ToImmutableArray();
    }

    internal static void Validate(RecordRetirementAttempt attempt)
    {
        for (var index = 0; index < attempt.Images.Length; index++)
        {
            var image = attempt.Images[index];
            if (image is null || image.Binding is null || image.Source is null || image.Result is null) throw RecordRetirementCodec.Invalid();
            var primary = image.Binding.Primary;
            if (primary is null || primary.RunId != attempt.Receipt.Facts.RunId || primary.AttemptId != attempt.Receipt.Facts.AttemptId ||
                primary.ParentJournalId != attempt.Receipt.JournalId || primary.ParentOperationId != attempt.Receipt.OperationId ||
                image.Position != index || !Enum.IsDefined(image.Outcome) ||
                image.Width.HasValue != image.Height.HasValue || image.Width is < 1 or > 8192 || image.Height is < 1 or > 8192 ||
                (long?)image.Width * image.Height > 33554432 || image.Outcome == RunImageOutcome.Captured && image.Width is null ||
                image.ReasonCode is not { Length: > 0 and <= 120 } ||
                (image.Outcome == RunImageOutcome.Captured) != (image.Png is not null) || image.Png is not null && image.Root is null)
                throw RecordRetirementCodec.Invalid();
            ImageCatalogPolicy.Validate(image.ToEntry().Item.Key);
            if (image.FrameId is { } frameId && (image.Outcome != RunImageOutcome.Captured ||
                frameId == image.Binding.CaptureId || !Enumerable.Range(1, ExecutionCaptureInputs.MaximumBatchFrames - 1)
                    .Any(index => CaptureFramePolicy.FrameId(image.Binding.CaptureId, index) == frameId)))
                throw RecordRetirementCodec.Invalid();
            StepEvidenceCodec.Plan(image.Source); StepEvidenceCodec.Plan(image.Result);
            StepEvidenceCodec.Plan(image.ParentClosure);
            if (image.Root is { } root) StepEvidenceCodec.Plan(root);
            if (image.Png is { } png && (png.SizeBytes is < 57 or > ExecutionCaptureInputs.MaximumPngBytes ||
                !RecordRetirementCodec.Digest(png.Sha256) || png.RelativePath != $"blobs/content/{png.Sha256}.bin"))
                throw RecordRetirementCodec.Invalid();
            if (!attempt.Writers.Any(writer => writer.Binding == primary && writer.Role == "process" && writer.Closure == image.ParentClosure))
                throw RecordRetirementCodec.Invalid();
        }
        if (attempt.Images.Select(value => value.ToEntry().Item.Key.CaptureId).Distinct().Count() != attempt.Images.Length)
            throw RecordRetirementCodec.Invalid();
    }

    internal static void RequireCatalogInside(SqliteConnection connection, SqliteTransaction transaction, RecordRetirementAttempt attempt)
    {
        var source = StartSourceRows.Read(connection, transaction, attempt.Receipt.OperationId);
        var expected = new ImageCatalogSnapshot(attempt.Receipt, attempt.Revision, attempt.JournalSha256,
            attempt.Images.Select(value => value.ToEntry()).ToArray())
        {
            StartSource = source,
            Bindings = attempt.Images.ToDictionary(value => value.ToEntry().Item.Key.CaptureId, value => value.Binding)
        };
        ImageCatalogRows.RequireSaved(connection, transaction, expected);
        foreach (var image in attempt.Images.Where(value => value.Png is not null))
        {
            var png = image.Png!;
            using var command = RecordRetirementRows.Command(connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM record_assets a JOIN blobs b ON b.digest=a.digest " +
                "WHERE a.run_id=$0 AND a.digest=$1 AND a.kind=0 AND b.size_bytes=$2 AND b.relative_path=$3);",
                attempt.Receipt.Facts.RunId, png.Sha256, png.SizeBytes, png.RelativePath);
            if ((long)command.ExecuteScalar()! != 1) throw RecordRetirementCodec.Invalid();
        }
    }
}
