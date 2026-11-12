using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class PayloadSourceCodec(RuntimeInstance instance, ContractReader contracts)
{
    internal const string Format = "nexus.extension.apply-source";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);

    internal BlobReference Persist(ExtensionPayloadSource source)
    {
        Validate(source);
        return _blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(source, Options));
    }

    internal ExtensionPayloadSource ReadMetadata(BlobReference reference)
    {
        RequirePlan(reference);
        using var input = _blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var strict = StrictJson.Parse(bytes);
        var source = JsonSerializer.Deserialize<ExtensionPayloadSource>(bytes, Options) ?? throw Invalid();
        Validate(source); return source;
    }

    internal void RequireFrozenBinding(ExtensionPayloadSource source, FrozenPayloadApply plan)
    {
        Validate(source);
        if (plan.InstanceId != source.InstanceId || plan.Kind != NexusPipeline.Core.Configuration.JournalKind.ExtensionApply ||
            source.OperationId != plan.OperationId || source.JournalId != plan.JournalId ||
            source.ExtensionId != plan.ExtensionId || source.Action != plan.Action ||
            !source.Claims.Select(claim => claim.ResourceKey).SequenceEqual(plan.ResourceKeys)) throw Invalid();
        var targetEntry = PhysicalResourceAdmission.EntryKey(plan.ParentIdentity, Path.GetFileName(plan.TargetPath));
        var workspaceEntry = PhysicalResourceAdmission.EntryKey(plan.ParentIdentity, Path.GetFileName(ExtensionWorkspacePath.For(plan)));
        if (!source.Claims.Any(claim => claim.ResourceKey == targetEntry && claim.Mode == ResourceClaimMode.Mutation) ||
            !source.Claims.Any(claim => claim.ResourceKey == workspaceEntry && claim.Mode == ResourceClaimMode.Mutation) ||
            plan.Before.OriginalObjects.Any(item => !source.Claims.Any(claim =>
                claim.ResourceKey == $"filesystem:object:{item.Identity.VolumeSerial}:{item.Identity.FileId}" &&
                claim.Mode == ResourceClaimMode.Mutation))) throw Invalid();
        if (source.Package is { } package)
        {
            var manifest = plan.Candidate.Snapshot.Entries.SingleOrDefault(entry => entry.RelativePath == "manifest.json");
            if (manifest?.Sha256 != package.Entry.ManifestSha256) throw Invalid();
        }
    }

    [SupportedOSPlatform("windows")]
    internal void RequirePackageContents(ExtensionPayloadSource source, FrozenPayloadApply plan,
        ExtensionCatalogVerifier verifier, StateDatabase database, CancellationToken cancellation)
    {
        RequireFrozenBinding(source, plan);
        if (source.Package is not { } package) return;
        if (database.InstanceId != instance.InstanceId ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.RuntimeRoot, instance.RuntimeRoot)) throw Invalid();
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            ExtensionCatalogRows.RequireAcceptedInside(connection, transaction, package.Catalog);
            transaction.Commit();
        }
        var catalog = verifier.Verify(Read(package.Catalog.Pointer), Read(package.Catalog.Catalog),
            Read(package.Catalog.Signature), package.Catalog.Identity.Channel);
        if (catalog.Identity != package.Catalog.Identity || catalog.KeyId != package.Catalog.KeyId ||
            catalog.Entries.SingleOrDefault(entry => entry.ExtensionId == source.ExtensionId) != package.Entry) throw Invalid();
        using var zipInput = _blobs.OpenVerified(package.Archive);
        using var zip = ExtensionPackageArchive.Open(zipInput, cancellation);
        var originalManifest = zip.ReadManifest(cancellation);
        var manifest = new ExtensionManifestAdmission(contracts).ReadCurrent(originalManifest, source.ExtensionId);
        VerifiedExtensionPackage.RequireManifest(package.Entry, manifest, originalManifest, zip.Files);
        VerifiedExtensionPackage.RequireContents(manifest, zip);
        var expected = plan.Candidate.Snapshot.Entries;
        if (expected.Count != zip.Directories.Length + zip.Files.Length ||
            zip.Directories.Any(path => !expected.Any(entry => entry.RelativePath == path &&
                entry.Kind == NexusPipeline.Core.Configuration.FileObjectKind.Directory && entry.SizeBytes == 0 && entry.Sha256 is null)) ||
            zip.Files.Any(file => !expected.Any(entry => entry.RelativePath == file.RelativePath &&
                entry.Kind == NexusPipeline.Core.Configuration.FileObjectKind.File &&
                entry.SizeBytes == file.SizeBytes && entry.Sha256 == file.Sha256))) throw Invalid();
    }

    internal BlobReference? ReadOriginalArchive(ExtensionPayloadSource source, FrozenPayloadApply plan, PayloadApplyRoots roots)
    {
        if (plan.Before.Snapshot.Shape == NexusPipeline.Core.Configuration.FileObjectKind.Missing) return null;
        var original = new InstalledFootprintCodec(instance, roots, contracts)
            .ReadMetadata(source.OriginalFootprint ?? throw Invalid());
        if (!original.IsInstalled || original.ExtensionId != source.ExtensionId ||
            original.Revision != source.ExpectedInstalledRevision || original.TargetPath != plan.TargetPath ||
            original.ParentIdentity != plan.ParentIdentity) throw Invalid();
        var previous = ReadMetadata(original.Source);
        if (previous.ExtensionId != original.ExtensionId || previous.JournalId != original.JournalId ||
            previous.OperationId != original.OperationId || previous.Package is not { } package ||
            package.Archive.Sha256 != original.Package.ArchiveSha256 ||
            package.Entry.ManifestSha256 != original.Package.ManifestSha256 ||
            package.Entry.PackageVersion != original.Package.PackageVersion) throw Invalid();
        return package.Archive;
    }

    internal static ImmutableArray<BlobReference> References(ExtensionPayloadSource source, bool includeArchive = true)
    {
        var references = ImmutableArray.CreateBuilder<BlobReference>();
        if (source.OriginalFootprint is { } original) references.Add(original);
        if (source.Package is { } package)
        {
            references.AddRange(ImmutableArray.Create(package.Catalog.Pointer, package.Catalog.Catalog, package.Catalog.Signature));
            if (includeArchive) references.Add(package.Archive);
        }
        return references.Distinct().ToImmutableArray();
    }

    private void Validate(ExtensionPayloadSource source)
    {
        if (source.Format != Format || source.Version != 1 || source.InstanceId != instance.InstanceId ||
            source.OperationId == Guid.Empty || source.JournalId == Guid.Empty || !Enum.IsDefined(source.Action) ||
            source.ExpectedInstalledRevision is < 0 or > 9007199254740991 || source.SettingsRevision is < 0 or > 9007199254740991 ||
            source.Claims.IsDefaultOrEmpty || source.Claims.Length > 4096) throw Invalid();
        if (source.ExtensionId is not { Length: >= 1 and <= 80 } ||
            source.ExtensionId[0] is not (>= 'a' and <= 'z') || source.ExtensionId[^1] == '-' ||
            source.ExtensionId.Contains("--", StringComparison.Ordinal) ||
            source.ExtensionId.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-'))) throw Invalid();
        if (source.Action is PayloadAction.Remove or PayloadAction.Upgrade && source.ExpectedInstalledRevision == 0 ||
            (source.ExpectedInstalledRevision == 0) != (source.OriginalFootprint is null) ||
            (source.Action == PayloadAction.Remove) != (source.Package is null)) throw Invalid();
        var prior = "";
        foreach (var claim in source.Claims)
        {
            if (claim is null || !Enum.IsDefined(claim.Mode) || claim.ResourceKey is not { Length: >= 1 and <= 512 } ||
                claim.ResourceKey.Any(char.IsControl) || StringComparer.Ordinal.Compare(prior, claim.ResourceKey) >= 0) throw Invalid();
            prior = claim.ResourceKey;
            if (claim.ResourceKey is not ("blobs:content" or "settings:host" or "extensions:catalog") &&
                claim.ResourceKey != "extension:" + source.ExtensionId &&
                !claim.ResourceKey.StartsWith("filesystem:entry:", StringComparison.Ordinal) &&
                !claim.ResourceKey.StartsWith("filesystem:object:", StringComparison.Ordinal)) throw Invalid();
            if (claim.ResourceKey is "blobs:content" or "extensions:catalog" &&
                claim.Mode != ResourceClaimMode.Reference) throw Invalid();
            if (claim.ResourceKey.StartsWith("filesystem:", StringComparison.Ordinal)) RequirePhysicalKey(claim.ResourceKey);
        }
        RequireClaim("extension:" + source.ExtensionId, ResourceClaimMode.Mutation);
        RequireClaim("blobs:content", ResourceClaimMode.Reference);
        RequireClaim("settings:host", source.InitializeEnabledPreference ? ResourceClaimMode.Mutation : ResourceClaimMode.Reference);
        if (source.InitializeEnabledPreference && (source.Action != PayloadAction.Install ||
            source.ExpectedInstalledRevision != 0 || !source.DesiredEnabled)) throw Invalid();
        if (source.Package is { } package)
        {
            RequireClaim("extensions:catalog", ResourceClaimMode.Reference);
            if (package.Entry is null || package.Catalog is null || package.Catalog.Identity is null || package.Archive is null ||
                package.Entry.ExtensionId != source.ExtensionId || package.Entry.Kind is not ("managed-code" or "data-specialized") ||
                package.Entry.SdkVersion != "0.1.0" || package.Entry.ArtifactName is not { Length: >= 1 and <= 120 } ||
                package.Entry.PackageVersion is not { Length: >= 1 and <= 80 } ||
                package.Entry.MinHostVersion is not { Length: >= 1 and <= 80 } ||
                package.Entry.ArtifactUrl is not { Length: >= 1 and <= 2048 } ||
                package.Catalog.KeyId is not { Length: >= 2 and <= 80 } || !Digest(package.Entry.ArtifactSha256) ||
                !Digest(package.Entry.ManifestSha256) || package.Archive.RelativePath !=
                $"blobs/content/{package.Entry.ArtifactSha256}.bin" || package.Archive.SizeBytes != package.Entry.ArtifactBytes ||
                package.Archive.Sha256 != package.Entry.ArtifactSha256 || package.Archive.SizeBytes is < 1 or > 268435456) throw Invalid();
            CatalogSequencePolicy.Require(package.Catalog.Identity, 1, null);
            RequirePlan(package.Catalog.Pointer); RequirePlan(package.Catalog.Catalog); RequirePlan(package.Catalog.Signature);
            if (package.Catalog.Catalog.Sha256 != package.Catalog.Identity.Sha256) throw Invalid();
        }
        if (source.OriginalFootprint is { } footprint) RequirePlan(footprint);
        void RequireClaim(string key, ResourceClaimMode mode)
        {
            if (!source.Claims.Any(claim => claim.ResourceKey == key && claim.Mode == mode)) throw Invalid();
        }
    }

    private byte[] Read(BlobReference reference)
    {
        using var input = _blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }
    private static void RequirePhysicalKey(string key)
    {
        var parts = key.Split(':');
        if (parts.Length is not (4 or 5) || parts[0] != "filesystem" ||
            !Hex(parts[2], 16) || !Hex(parts[3], 32) ||
            (parts[1] == "object" ? parts.Length != 4 : parts[1] != "entry" || parts.Length != 5 || !Digest(parts[4])))
            throw Invalid();
        static bool Hex(string value, int count) => value.Length == count &&
            value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
    private static bool Digest(string value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void RequirePlan(BlobReference? reference)
    {
        if (reference is null || reference.SizeBytes is < 1 or > 2097152 || reference.Sha256 is not { Length: 64 } ||
            reference.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            reference.RelativePath != $"blobs/plans/{reference.Sha256}.json") throw Invalid();
    }
    private static IOException Invalid() => new("extension_payload_source_unbound");
}
