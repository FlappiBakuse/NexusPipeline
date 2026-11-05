namespace NexusPipeline.Core.Records;

public enum RunImageOutcome { Captured, Unavailable }
public sealed record RunImageKey(Guid RunId, Guid AttemptId, string StepId, Guid CaptureId);
public sealed record ImageCatalogQuery(Guid RunId, Guid AttemptId, int AfterPosition = -1, int Limit = 50);
public sealed record ImageCatalogItem(int Position, RunImageKey Key, RunImageOutcome Outcome, string ReasonCode,
    int? Width, int? Height, string SourceSha256, string? RootSha256, string ResultSha256,
    string? ImageSha256, long? SizeBytes)
{
    public RecordContentAvailability ContentAvailability { get; init; }
    public int InvocationOrdinal { get; init; } = 1;
}
public sealed record ImageCatalogPage(IReadOnlyList<ImageCatalogItem> Items, int? NextPosition);

public interface IRunImageCatalog
{
    ImageCatalogPage ReadPage(ImageCatalogQuery query);
    Stream OpenOriginal(RunImageKey key, string resultSha256);
}

public static class ImageCatalogPolicy
{
    public static void Validate(ImageCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.RunId == Guid.Empty || query.AttemptId == Guid.Empty || query.AfterPosition is < -1 or > 4095 ||
            query.Limit is < 1 or > 100) throw new ArgumentException("record_image_query_invalid");
    }

    public static void Validate(RunImageKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.RunId == Guid.Empty || key.AttemptId == Guid.Empty || key.CaptureId == Guid.Empty ||
            key.StepId is not { Length: > 0 and <= 120 } || key.StepId.Any(character =>
                character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.')))
            throw new ArgumentException("record_image_query_invalid");
    }
}
