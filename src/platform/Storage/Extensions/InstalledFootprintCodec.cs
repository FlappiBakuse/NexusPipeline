using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class InstalledFootprintCodec(RuntimeInstance instance, PayloadApplyRoots roots,
    ContractReader contracts)
{
    internal const string Format = "nexus.extension.installation-footprint";
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal ExtensionInstalledFootprint ReadMetadata(BlobReference reference)
    {
        RequirePlan(reference);
        using var input = _blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var strict = StrictJson.Parse(bytes);
        var value = JsonSerializer.Deserialize<ExtensionInstalledFootprint>(bytes, Options) ?? throw Invalid();
        Validate(value);
        if (!JsonElement.DeepEquals(strict.RootElement, JsonSerializer.SerializeToElement(value, Options))) throw Invalid();
        return Freeze(value);
    }

    internal void RequireBinding(ExtensionInstalledFootprint footprint, ExtensionInstalledMetadata saved,
        FrozenPayloadApply original, BlobReference originalPlanSource,
        ExtensionPhysicalEntry physical)
    {
        Validate(footprint);
        if (original.Kind != JournalKind.ExtensionApply || original.InstanceId != instance.InstanceId ||
            footprint.ExtensionId != saved.ExtensionId || footprint.Revision != saved.Revision ||
            footprint.IsInstalled != saved.IsInstalled || footprint.Package != saved.Package ||
            footprint.JournalId != saved.JournalId || original.JournalId != saved.JournalId ||
            footprint.OperationId != original.OperationId || footprint.TargetPath != original.TargetPath ||
            footprint.ParentIdentity != original.ParentIdentity || original.ExtensionSource != originalPlanSource ||
            footprint.Source != originalPlanSource || footprint.PhysicalResult != physical.Intent.Artifact ||
            physical.Record.Stage != ExtensionPhysicalCodec.Sequence(original)[^1] || footprint.IsInstalled != (original.Action != PayloadAction.Remove))
            throw Invalid();
        var expected = footprint.IsInstalled ? original.Candidate : new ArchivedFileTree(new(FileObjectKind.Missing, []), [], []);
        if (footprint.Payload.Snapshot.Shape != expected.Snapshot.Shape ||
            !footprint.Payload.Snapshot.Entries.SequenceEqual(expected.Snapshot.Entries) ||
            !footprint.Payload.Contents.SequenceEqual(expected.Contents) ||
            (footprint.IsInstalled ? !footprint.Payload.OriginalObjects.SequenceEqual(physical.Record.CandidateObjects) :
                footprint.Payload.OriginalObjects.Count != 0)) throw Invalid();
        var source = new PayloadSourceCodec(instance, contracts).ReadMetadata(originalPlanSource);
        if (source.OperationId != original.OperationId || source.JournalId != original.JournalId ||
            footprint.Revision != checked(source.ExpectedInstalledRevision + 1)) throw Invalid();
        if (source.Package is { } package)
        {
            var identity = new ExtensionPackageIdentity(source.ExtensionId, package.Entry.PackageVersion,
                package.Entry.Kind == "managed-code" ? ExtensionPackageKind.ManagedCode : ExtensionPackageKind.DataSpecialized,
                package.Entry.ArtifactSha256, package.Entry.ManifestSha256);
            if (footprint.Package != identity) throw Invalid();
        }
        else
        {
            if (source.OriginalFootprint is null) throw Invalid();
            var previous = ReadMetadata(source.OriginalFootprint);
            if (previous.ExtensionId != source.ExtensionId || previous.Revision != source.ExpectedInstalledRevision ||
                !previous.IsInstalled || previous.Package != footprint.Package) throw Invalid();
        }
    }

    internal static IReadOnlyList<BlobReference> References(ExtensionInstalledFootprint value) =>
        Array.AsReadOnly(value.Payload.Contents.Prepend(value.Source).Prepend(value.PhysicalResult).Distinct().ToArray());

    internal byte[] EncodeMetadata(ExtensionInstalledFootprint value)
    {
        Validate(value);
        return BoundedJson.Serialize(Freeze(value), Options);
    }

    private void Validate(ExtensionInstalledFootprint value)
    {
        if (value is null || value.Format != Format || value.Version != 1 || value.InstanceId != instance.InstanceId ||
            value.OperationId == Guid.Empty || value.JournalId == Guid.Empty ||
            value.Revision is < 1 or > 9007199254740991 || value.Package is null ||
            value.ExtensionId != value.Package.ExtensionId || value.ParentIdentity is null || value.Payload?.Snapshot?.Entries is null ||
            value.Payload.OriginalObjects is null || value.Payload.Contents is null ||
            value.Payload.Snapshot.Entries.Any(item => item is null) || value.Payload.OriginalObjects.Any(item => item is null) ||
            value.Payload.Contents.Any(item => item is null))
            throw Invalid();
        RequireIdentity(value.ParentIdentity); RequirePlan(value.Source); RequirePlan(value.PhysicalResult);
        var package = value.Package;
        if (!Enum.IsDefined(package.Kind) || !Digest(package.ArchiveSha256) || !Digest(package.ManifestSha256) ||
            package.PackageVersion is not { Length: >= 1 and <= 80 } || package.PackageVersion.Any(char.IsControl) ||
            value.ExtensionId is not { Length: >= 1 and <= 80 } || value.ExtensionId[0] is not (>= 'a' and <= 'z') ||
            value.ExtensionId[^1] == '-' || value.ExtensionId.Contains("--", StringComparison.Ordinal) ||
            value.ExtensionId.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-'))) throw Invalid();
        var expected = PhysicalPathPolicy.NormalizeSyntax(Path.Combine(roots.ExtensionPayloadRoot, value.ExtensionId));
        if (value.TargetPath != expected || value.TargetPath == Path.GetPathRoot(value.TargetPath)) throw Invalid();
        FileTreeArchive.ValidateArchive(value.Payload);
        if (value.IsInstalled ? value.Payload.Snapshot.Shape != FileObjectKind.Directory :
            value.Payload.Snapshot.Shape != FileObjectKind.Missing) throw Invalid();
        var entries = value.Payload.Snapshot.Entries;
        if (entries.Count > 4096 || value.Payload.OriginalObjects.Count != entries.Count || value.Payload.Contents.Count > 1024 ||
            entries.Where(entry => entry.Kind == FileObjectKind.File).Sum(entry => entry.SizeBytes) > 268435456 ||
            value.Payload.OriginalObjects.Select(item => item.Identity).Distinct().Count() != entries.Count ||
            value.Payload.OriginalObjects.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count ||
            value.Payload.OriginalObjects.Any(item => !entries.Any(entry => entry.RelativePath == item.RelativePath)))
            throw Invalid();
        foreach (var item in value.Payload.OriginalObjects)
        {
            RequireIdentity(item.Identity);
            if (item.Identity.VolumeSerial != value.ParentIdentity.VolumeSerial || item.Identity == value.ParentIdentity) throw Invalid();
        }
        foreach (var content in value.Payload.Contents)
            if (!Digest(content.Sha256) || content.SizeBytes is < 0 or > 268435456 ||
                content.RelativePath != "blobs/content/" + content.Sha256 + ".bin") throw Invalid();
    }

    private static ExtensionInstalledFootprint Freeze(ExtensionInstalledFootprint value) => value with
    {
        Payload = value.Payload with
        {
            Snapshot = value.Payload.Snapshot with { Entries = Array.AsReadOnly(value.Payload.Snapshot.Entries.ToArray()) },
            OriginalObjects = Array.AsReadOnly(value.Payload.OriginalObjects.ToArray()),
            Contents = Array.AsReadOnly(value.Payload.Contents.ToArray())
        }
    };
    private static void RequireIdentity(PhysicalObjectIdentity value)
    {
        if (value is null || !Hex(value.VolumeSerial, 16) || !Hex(value.FileId, 32)) throw Invalid();
    }
    private static bool Hex(string value, int length) => value is not null && value.Length == length &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Digest(string value) => Hex(value, 64);
    private static void RequirePlan(BlobReference reference)
    {
        if (reference is null || reference.SizeBytes is < 1 or > 2097152 || !Digest(reference.Sha256) ||
            reference.RelativePath != "blobs/plans/" + reference.Sha256 + ".json") throw Invalid();
    }
    private static IOException Invalid() => new("extension_installed_footprint_unbound");
}
