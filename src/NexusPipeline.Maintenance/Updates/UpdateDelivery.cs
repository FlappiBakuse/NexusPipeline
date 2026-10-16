using System.IO.Compression;
using System.Security.Cryptography;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal sealed record UpdateDeliveryResult(string Transport, long NetworkBytes, long ReusedBytes, string Reason,
    UpdateStoragePlan? StoragePlan = null, UpdateTransportReceipt? Receipt = null);

internal static class UpdateDelivery
{
    internal static async Task<UpdateDeliveryResult> PrepareAsync(HttpClient http, UpdateSourcePolicy policy, ReleaseInfo release,
        string installation, string currentVersion, string zipPath, string checksumPath, string staging,
        Func<string, string?, bool, UpdatePayloadIdentity> freeze, UpdateCreatedArtifacts artifacts,
        IProgress<UpdateDownloadProgress>? progress, CancellationToken token)
    {
        if (release.DeltaAsset is not null)
            return await UpdateRecipeDelivery.PrepareAsync(http, policy, release, installation, currentVersion,
                zipPath, checksumPath, staging, freeze, artifacts, progress, token).ConfigureAwait(false);
        async Task FullAsync(UpdateTargetManifest? target)
        {
            await UpdateAssetDownloader.DownloadAsync(http, policy, release.ZipAsset, zipPath, progress, token, artifacts).ConfigureAwait(false);
            await UpdateAssetDownloader.WriteChecksumAsync(checksumPath, release.ZipAsset.Sha256, token, artifacts).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            string? error = UpdatePackage.ExtractApplication(zipPath, staging, release.VersionText, freeze, artifacts);
            if (error is not null) throw new InvalidDataException(error);
            if (target is not null) ValidateStaging(staging, target, freeze);
        }
        if (release.TargetManifestAsset is null)
        {
            var storage = UpdateStoragePlan.ForInstallation(installation, 0, release.ZipAsset.SizeBytes, UpdatePackage.MaxExtractedBytes);
            storage.RequireAvailable(installation);
            await FullAsync(null).ConfigureAwait(false);
            return new("full", release.ZipAsset.SizeBytes, 0, "legacy-release", storage);
        }
        artifacts.EnsureDirectory(Path.GetDirectoryName(zipPath)!);
        string deliveryRoot = zipPath + ".delivery";
        var temporary = new UpdateCreatedArtifacts(deliveryRoot);
        try
        {
            if (File.Exists(deliveryRoot) || Directory.Exists(deliveryRoot)) throw new IOException("update.delivery_exists");
            temporary.EnsureDirectory(deliveryRoot);
            string manifestPath = Path.Combine(deliveryRoot, "target.json");
            await UpdateAssetDownloader.DownloadAsync(http, policy, release.TargetManifestAsset, manifestPath, null, token, temporary).ConfigureAwait(false);
            var target = UpdateTargetManifest.Parse(await File.ReadAllBytesAsync(manifestPath, token).ConfigureAwait(false), release);
            _ = freeze(installation, currentVersion, true);
            var reused = target.Components.Keys.Where(id => target.Files.Where(file => file.ComponentId == id)
                .All(file => Matches(installation, file))).ToHashSet(StringComparer.Ordinal);
            long requiredBytes = target.Components.Where(item => !reused.Contains(item.Key)).Sum(item => item.Value.SizeBytes);
            var storage = UpdateStoragePlan.ForInstallation(installation, requiredBytes, release.ZipAsset.SizeBytes,
                target.Files.Sum(file => file.SizeBytes));
            storage.RequireAvailable(installation);
            if (requiredBytes >= release.ZipAsset.SizeBytes)
            {
                await FullAsync(target).ConfigureAwait(false);
                return new("full", release.TargetManifestAsset.SizeBytes + release.ZipAsset.SizeBytes, 0, "components-not-smaller", storage);
            }
            long receivedBytes = release.TargetManifestAsset.SizeBytes;
            try
            {
                foreach (var component in target.Components.Where(item => !reused.Contains(item.Key)))
                {
                    long offset = receivedBytes;
                    long observed = 0;
                    var componentProgress = new InlineProgress(value =>
                    {
                        observed = value.BytesRead;
                        progress?.Report(new(offset + value.BytesRead, release.TargetManifestAsset.SizeBytes + requiredBytes));
                    });
                    try
                    {
                        await UpdateAssetDownloader.DownloadAsync(http, policy, component.Value, Path.Combine(deliveryRoot, component.Key + ".zip"),
                            componentProgress, token, temporary).ConfigureAwait(false);
                    }
                    finally { receivedBytes += observed; }
                }
            }
            catch (UpdateTransportException)
            {
                token.ThrowIfCancellationRequested();
                await FullAsync(target).ConfigureAwait(false);
                return new("full", receivedBytes + release.ZipAsset.SizeBytes, 0, "component-transport-unavailable", storage);
            }
            if (File.Exists(staging) || Directory.Exists(staging)) throw new IOException("update.staging_exists");
            artifacts.EnsureDirectory(staging);
            foreach (string component in UpdateTargetManifest.ComponentIds)
            {
                token.ThrowIfCancellationRequested();
                var selected = target.Files.Where(file => file.ComponentId == component).ToArray();
                if (reused.Contains(component))
                {
                    foreach (var file in selected)
                    {
                        string source = Path.Combine(installation, file.Path);
                        PayloadPathSafety.RequireLinkFree(source);
                        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                        CopyFile(input, staging, file, artifacts, token);
                    }
                }
                else
                {
                    using var archive = ZipFile.OpenRead(Path.Combine(deliveryRoot, component + ".zip"));
                    if (archive.Entries.Count != selected.Length || archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != selected.Length
                        || !archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).SequenceEqual(selected.Select(file => file.Path).Order(StringComparer.Ordinal)))
                        throw new InvalidDataException("update.component_file_set");
                    foreach (var file in selected)
                    {
                        var entry = archive.GetEntry(file.Path)!;
                        int mode = (entry.ExternalAttributes >> 16) & 0xf000;
                        if (mode is not (0 or 0x8000) || entry.Length != file.SizeBytes
                            || entry.Length > 0 && (entry.CompressedLength <= 0 || entry.Length / (double)entry.CompressedLength > UpdatePackage.MaxCompressionRatio))
                            throw new InvalidDataException("update.component_entry");
                        using var input = entry.Open();
                        CopyFile(input, staging, file, artifacts, token);
                    }
                }
            }
            ValidateStaging(staging, target, freeze);
            CreateLocalPackage(zipPath, staging, target.Files, artifacts, token);
            await UpdateAssetDownloader.WriteChecksumAsync(checksumPath, artifacts.FileHash(zipPath), token, artifacts).ConfigureAwait(false);
            ValidateStaging(staging, target, freeze);
            return new("components", receivedBytes, target.Files.Where(file => reused.Contains(file.ComponentId)).Sum(file => file.SizeBytes), "verified-local-components", storage);
        }
        finally { temporary.Cleanup(); }
    }

    private static bool Matches(string root, UpdateTargetFile file)
    {
        string path = Path.Combine(root, file.Path);
        PayloadPathSafety.RequireLinkFree(path);
        if (!File.Exists(path)) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return stream.Length == file.SizeBytes && Convert.ToHexStringLower(SHA256.HashData(stream)) == file.Sha256;
    }

    internal static void CopyFile(Stream input, string staging, UpdateTargetFile file, UpdateCreatedArtifacts artifacts, CancellationToken token)
    {
        string destination = Path.Combine(staging, file.Path);
        artifacts.EnsureDirectory(Path.GetDirectoryName(destination)!);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        string hash;
        try
        {
            byte[] buffer = new byte[64 * 1024];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = input.Read(buffer);
                if (read == 0) break;
                if (written + read > file.SizeBytes) throw new InvalidDataException("update.component_size");
                output.Write(buffer, 0, read);
                digest.AppendData(buffer, 0, read);
                written += read;
            }
        }
        finally
        {
            hash = Convert.ToHexStringLower(digest.GetHashAndReset());
            artifacts.FileCreated(destination, written, hash);
        }
        if (written != file.SizeBytes || hash != file.Sha256) throw new InvalidDataException("update.component_file_digest");
    }

    private static void ValidateStaging(string staging, UpdateTargetManifest target, Func<string, string?, bool, UpdatePayloadIdentity> freeze)
    {
        var identity = freeze(staging, target.Version, false);
        if (identity.BuildId != target.BuildId || identity.FrontendHash != target.FrontendHash
            || identity.ManifestSha256 != target.PayloadManifestSha256 || identity.Generation != InstallationGeneration.Id
            || target.Files.Any(file => !Matches(staging, file))) throw new InvalidDataException("update.target_payload_identity");
    }

    internal static void CreateLocalPackage(string destination, string staging, IReadOnlyList<UpdateTargetFile> files,
        UpdateCreatedArtifacts artifacts, CancellationToken token)
    {
        bool created = false;
        try
        {
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            using var archive = new ZipArchive(output, ZipArchiveMode.Create);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(file.Path, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var input = new FileStream(Path.Combine(staging, file.Path), FileMode.Open, FileAccess.Read, FileShare.Read);
                using var stream = entry.Open();
                input.CopyTo(stream);
            }
        }
        finally
        {
            if (created && File.Exists(destination))
            {
                using var stream = File.OpenRead(destination);
                artifacts.FileCreated(destination, stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)));
            }
        }
    }

    private sealed class InlineProgress(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => report(value);
    }
}
