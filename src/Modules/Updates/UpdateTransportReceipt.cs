namespace NexusPipeline.Modules.Updates;

internal sealed record UpdateTransportReceipt(string Kind, UpdateReleaseAsset Asset, string RecipeSha256,
    string SourceManifestSha256, long NetworkBytes, long ReusedBytes)
{
    internal void Validate(UpdateReleaseEvidence? evidence)
    {
        if (Kind is not ("delta" or "full") || evidence is not { Protocol: 2 }
            || !evidence.Assets.Contains(Asset) || Asset.Name != $"NexusPipeline-{evidence.Tag}-win-x64-{(Kind == "delta" ? "Update" : "Portable")}.zip"
            || !UpdateInventory.Hash(RecipeSha256) || !UpdateInventory.Hash(SourceManifestSha256)
            || NetworkBytes < Asset.SizeBytes || ReusedBytes < 0 || ReusedBytes > UpdatePackage.MaxExtractedBytes
            || Kind == "full" && ReusedBytes != 0)
            throw new InvalidDataException("update.transport_receipt_invalid");
    }
}
