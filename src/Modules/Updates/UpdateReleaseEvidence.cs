namespace NexusPipeline.Modules.Updates;

internal sealed record UpdateReleaseEvidence(long ReleaseId, string Tag, string TargetBuildId,
    string RouteId, IReadOnlyList<UpdateReleaseAsset> Assets)
{
    public int Protocol { get; init; } = 1;
    public bool Equals(UpdateReleaseEvidence? other) => other is not null && ReleaseId == other.ReleaseId
        && Protocol == other.Protocol && Tag == other.Tag && TargetBuildId == other.TargetBuildId && RouteId == other.RouteId && Assets.SequenceEqual(other.Assets);
    public override int GetHashCode() => HashCode.Combine(ReleaseId, Tag, TargetBuildId, RouteId, Protocol);

    internal static UpdateReleaseEvidence? Freeze(ReleaseInfo release, UpdatePayloadIdentity payload)
    {
        if (release.TargetManifestAsset is null && release.DeltaAsset is null) return null;
        var result = new UpdateReleaseEvidence(release.ReleaseId, release.Tag, payload.BuildId,
            UpdateMigrationRegistry.NoStoreChangeRoute, release.Assets!.Values.OrderBy(asset => asset.Name, StringComparer.Ordinal).ToArray())
            { Protocol = release.DeltaAsset is null ? 1 : 2 };
        result.Validate(release.VersionText, payload);
        return result;
    }

    internal void Validate(string version, UpdatePayloadIdentity payload)
    {
        string prefix = $"NexusPipeline-v{version}-win-x64";
        string[] required = Protocol == 2 ? [prefix + "-Setup.exe", prefix + "-Portable.zip", prefix + "-Update.zip"]
            : [prefix + ".zip", prefix + "-setup.exe", prefix + "-update.json",
            .. UpdateTargetManifest.ComponentIds.Select(id => prefix + "-" + id + ".zip")];
        if (ReleaseId <= 0 || Tag != "v" + version || TargetBuildId != payload.BuildId
            || Protocol is not (1 or 2) || RouteId != UpdateMigrationRegistry.NoStoreChangeRoute || Assets is null
            || (Protocol == 2 ? Assets.Count != 3 : Assets.Count is < 6 or > 400)
            || Assets.Any(asset => asset is null || asset.Id <= 0 || asset.Name is not { Length: > 0 and <= 256 }
                || asset.SizeBytes is < 1 or > UpdateCatalog.MaxDownloadBytes || !UpdateInventory.Hash(asset.Sha256)
                || !Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                || uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            || Assets.Select(asset => asset.Id).Distinct().Count() != Assets.Count
            || Assets.Select(asset => asset.Name).Distinct(StringComparer.Ordinal).Count() != Assets.Count
            || required.Any(name => !Assets.Any(asset => asset.Name == name)))
            throw new InvalidDataException("update.release_evidence_invalid");
    }
}

internal static class UpdateMigrationRegistry
{
    internal const string NoStoreChangeRoute = "g0170-no-store-change-v1";
    internal static readonly string[] MigratorIds = [];

    internal static void Validate(string route, UpdateInventory input, UpdateInventory output)
    {
        if (input is null || output is null) throw new InvalidDataException("update.migration_snapshot_missing");
        input.Validate(); output.Validate();
        if (route != NoStoreChangeRoute || input.Entries.Count != 0 || output.Entries.Count != 0)
            throw new InvalidDataException("update.migration_route_unavailable");
    }
}
