using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Archives;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed class UpdateSourceCodec(RuntimeInstance instance, ContractReader contracts)
{
    internal const string Format = "nexus.maintenance.update-source";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);

    internal BlobReference Persist(HostUpdateSource source)
    {
        Validate(source);
        return _blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(source, Options));
    }

    internal HostUpdateSource ReadMetadata(BlobReference reference)
    {
        RequirePlan(reference);
        var bytes = Read(reference);
        using var strict = StrictJson.Parse(bytes);
        var source = JsonSerializer.Deserialize<HostUpdateSource>(bytes, Options) ?? throw Invalid();
        Validate(source); return source;
    }

    internal HostUpdateRelease RequireFrozenBinding(HostUpdateSource source, FrozenPayloadApply plan)
    {
        Validate(source);
        if (source.InstanceId != plan.InstanceId || source.OperationId != plan.OperationId || source.JournalId != plan.JournalId ||
            plan.Kind != JournalKind.UpdateApply || plan.Action != PayloadAction.Upgrade || plan.ExtensionId is not null ||
            plan.ExtensionSource is not null || !plan.ResourceKeys.SequenceEqual(new[] { "maintenance:host-payload" })) throw Invalid();
        var feed = new UpdateFeedCodec(contracts).Read(Read(source.Feed.Feed), source.Feed.Identity.Channel);
        if (feed.Identity != source.Feed.Identity) throw Invalid();
        var release = feed.Releases.SingleOrDefault(item => item.HostVersion == source.HostVersion) ?? throw Invalid();
        if (source.PayloadManifest.Sha256 != release.PayloadManifestSha256 || source.Archive.Sha256 != release.Artifact.Sha256 ||
            source.Archive.SizeBytes != release.Artifact.SizeBytes) throw Invalid();
        var manifest = new UpdateManifestCodec(contracts).Read(Read(source.PayloadManifest), release, requiredProfile: null);
        ApplicationManifest.RequireTree(plan.Candidate, manifest.Application, source.PayloadManifest, allowMissing: false);
        if (source.Installation is not null)
        {
            if (!OperatingSystem.IsWindows()) throw Invalid();
            _ = new Installation.InstallationUpdateBinding(instance, contracts).Read(source.Installation, plan.TargetPath, plan.Before, profile: null);
        }
        return release;
    }

    internal void RequireAcceptedInside(SqliteConnection connection, SqliteTransaction transaction, HostUpdateSource source)
    {
        Validate(source);
        UpdateFeedRows.RequireAccepted(connection, transaction, source.Feed);
    }

    internal SavedUpdateFeed RequireTrustedContents(HostUpdateSource source, FrozenPayloadApply plan, StateDatabase database,
        UpdateFeedVerifier verifier, CancellationToken cancellation)
    {
        var head = RequireTrustedFeed(source, plan, database, verifier, cancellation);
        var release = RequireFrozenBinding(source, plan);
        using var input = _blobs.OpenVerified(source.Archive);
        using var archive = PayloadArchive.Open(input, new(4096, 1073741824), Invalid, PayloadArchiveKind.Application, cancellation);
        var originalManifest = archive.ReadManifest(cancellation);
        var manifest = new UpdateManifestCodec(contracts).Read(originalManifest, release, archive.Files, verifier.PayloadProfile);
        var recipe = UpdateRecipe.Read(archive, manifest, contracts);
        recipe.RequireBefore(plan.Before, manifest,
            new UpdateFeedCodec(contracts).Read(Read(source.Feed.Feed), source.Feed.Identity.Channel), _blobs, contracts);
        if (!originalManifest.AsSpan().SequenceEqual(Read(source.PayloadManifest)) ||
            archive.Files.Where(file => !file.RelativePath.StartsWith("package-info/", StringComparison.Ordinal))
            .Any(file => !plan.Candidate.Snapshot.Entries.Any(entry => entry.RelativePath == file.RelativePath &&
                entry.Kind == FileObjectKind.File && entry.SizeBytes == file.SizeBytes && entry.Sha256 == file.Sha256))) throw Invalid();
        UpdateBuildProfile.Require(manifest, plan.Candidate, _blobs, verifier.PayloadProfile);
        if (source.Installation is not null)
        {
            if (!OperatingSystem.IsWindows()) throw Invalid();
            _ = new Installation.InstallationUpdateBinding(instance, contracts).Read(source.Installation, plan.TargetPath, plan.Before, verifier.PayloadProfile);
        }
        RequireCurrentHead(source, database, head);
        return head;
    }

    internal SavedUpdateFeed RequireTrustedFeed(HostUpdateSource source, FrozenPayloadApply plan, StateDatabase database,
        UpdateFeedVerifier verifier, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var release = RequireFrozenBinding(source, plan);
        if (database.InstanceId != instance.InstanceId || !StringComparer.OrdinalIgnoreCase.Equals(database.RuntimeRoot, instance.RuntimeRoot)) throw Invalid();
        SavedUpdateFeed head;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            RequireAcceptedInside(connection, transaction, source);
            head = UpdateFeedRows.ReadHead(connection, transaction, source.Feed.Identity.Channel) ?? throw Invalid();
            transaction.Commit();
        }
        var current = verifier.Verify(Read(head.Feed), Read(head.Signature), head.Identity.Channel);
        if (current.Feed.Identity != head.Identity || current.KeyId != head.KeyId || head.Identity.Sequence < source.Feed.Identity.Sequence) throw Invalid();
        var verified = verifier.Verify(Read(source.Feed.Feed), Read(source.Feed.Signature), source.Feed.Identity.Channel);
        if (verified.Feed.Identity != source.Feed.Identity || verified.KeyId != source.Feed.KeyId ||
            verified.Feed.Releases.SingleOrDefault(item => item.HostVersion == source.HostVersion) != release ||
            !Version.TryParse(release.MinReaderVersion.Split('-')[0], out var minimum) || minimum > verifier.ReaderVersion) throw Invalid();
        RequireCurrentHead(source, database, head);
        return head;
    }

    private void RequireCurrentHead(HostUpdateSource source, StateDatabase database, SavedUpdateFeed head)
    {
        using var fresh = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var tx = fresh.BeginTransaction(deferred: true);
        RequireAcceptedInside(fresh, tx, source);
        if (UpdateFeedRows.ReadHead(fresh, tx, head.Identity.Channel) != head) throw Invalid();
        tx.Commit();
    }

    internal static ImmutableArray<BlobReference> References(HostUpdateSource source)
    {
        var result = ImmutableArray.Create(source.Feed.Feed, source.Feed.Signature, source.PayloadManifest, source.Archive);
        return source.Installation is null ? result : result.Add(source.Installation);
    }

    private void Validate(HostUpdateSource source)
    {
        if (source.Format != Format || source.Version != 1 || source.InstanceId != instance.InstanceId ||
            source.OperationId == Guid.Empty || source.JournalId == Guid.Empty || source.Feed?.Identity is null ||
            source.Feed.KeyId is not { Length: >= 2 and <= 80 } || source.Feed.KeyId.Any(char.IsControl) ||
            source.HostVersion is not { Length: >= 1 and <= 80 } || !Enum.IsDefined(source.Timing) ||
            source.Window is null || !Enum.IsDefined(source.Window.Mode) || source.Window.Route is not { Length: >= 1 and <= 256 } ||
            source.Window.Mode == UpdateWindowMode.Headless && (source.Window.Visible || source.Window.Route != "/") ||
            source.Window.Route[0] != '/' || source.Window.Route.StartsWith("//", StringComparison.Ordinal) ||
            source.Window.Route.Any(c => char.IsControl(c) || c is '\\' or '?' or '#' or '%' or ':')) throw Invalid();
        UpdateFeedPolicy.Require(source.Feed.Identity, 1, null);
        RequirePlan(source.Feed.Feed); RequirePlan(source.Feed.Signature); RequirePlan(source.PayloadManifest);
        if (source.Installation is not null) RequirePlan(source.Installation);
        if (source.Feed.Feed.Sha256 != source.Feed.Identity.Sha256 || source.Archive is null ||
            !Digest(source.Archive.Sha256) || source.Archive.SizeBytes is < 1 or > 1073741824 ||
            source.Archive.RelativePath != $"blobs/content/{source.Archive.Sha256}.bin") throw Invalid();
    }

    private byte[] Read(BlobReference reference)
    {
        using var input = _blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }
    private static bool Digest(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void RequirePlan(BlobReference? reference)
    {
        if (reference is null || reference.SizeBytes is < 1 or > StrictJson.MaximumBytes || !Digest(reference.Sha256) ||
            reference.RelativePath != $"blobs/plans/{reference.Sha256}.json") throw Invalid();
    }
    private static IOException Invalid() => new("maintenance_update_source_unbound");
}
