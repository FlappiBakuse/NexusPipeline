namespace NexusPipeline.Core.Records;

public enum RecordAssetKind { Screenshot, Log, Attachment }

public sealed record RecordAsset(Guid RunId, string Sha256, long SizeBytes, RecordAssetKind Kind)
{
    public void Validate()
    {
        if (RunId == Guid.Empty || SizeBytes is < 0 or > 1_073_741_824 || !Enum.IsDefined(Kind) || Sha256.Length != 64 ||
            Sha256.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new ArgumentException("record_asset_invalid");
    }
}

public sealed record RecordAssetCursor(string Sha256, RecordAssetKind Kind);
public sealed record RecordAssetPage(IReadOnlyList<RecordAsset> Items, RecordAssetCursor? Next);

public interface IAssetContentStore
{
    Stream OpenVerified(RecordAsset asset);
}
