using System.IO.Compression;
using System.Security.Cryptography;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal static class UpdateRecipeDelivery
{
    internal static async Task<UpdateDeliveryResult> PrepareAsync(HttpClient http, UpdateSourcePolicy policy, ReleaseInfo release,
        string installation, string currentVersion, string zipPath, string checksumPath, string staging,
        Func<string, string?, bool, UpdatePayloadIdentity> freeze, UpdateCreatedArtifacts artifacts,
        IProgress<UpdateDownloadProgress>? progress, CancellationToken token)
    {
        var installed = freeze(installation, currentVersion, true);
        bool delta = release.BaseManifestHint == installed.ManifestSha256 && release.DeltaAsset!.SizeBytes < release.ZipAsset.SizeBytes
            && File.Exists(Path.Combine(installation, "README.md"));
        var storage = UpdateStoragePlan.ForInstallation(installation, delta ? release.DeltaAsset!.SizeBytes : 0,
            release.ZipAsset.SizeBytes, UpdatePackage.MaxExtractedBytes);
        storage.RequireAvailable(installation);
        artifacts.EnsureDirectory(Path.GetDirectoryName(zipPath)!);
        string temporaryRoot = zipPath + ".delivery";
        if (File.Exists(temporaryRoot) || Directory.Exists(temporaryRoot)) throw new IOException("update.delivery_exists");
        var temporary = new UpdateCreatedArtifacts(temporaryRoot);
        long received = 0;
        string reason = delta ? "exact-baseline" : "complete-package";
        try
        {
            temporary.EnsureDirectory(temporaryRoot);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var asset = delta ? release.DeltaAsset! : release.ZipAsset;
                string package = Path.Combine(temporaryRoot, delta ? "update.zip" : "portable.zip");
                long observed = 0, offset = received;
                try
                {
                    await UpdateAssetDownloader.DownloadAsync(http, policy, asset, package,
                        new ProgressSink(value => { observed = value.BytesRead; progress?.Report(new(offset + value.BytesRead, offset + asset.SizeBytes)); }),
                        token, temporary).ConfigureAwait(false);
                }
                catch (UpdateTransportException) when (delta && attempt == 0)
                {
                    delta = false;
                    reason = "delta-transport-unavailable";
                    continue;
                }
                finally { received += observed; }
                using var archive = ZipFile.OpenRead(package);
                var (recipe, bytes) = ReadVerifiedRecipe(archive, release.VersionText, delta ? "update" : "portable");
                if (delta && recipe.Base is not null && (recipe.Base.PayloadManifestSha256 != installed.ManifestSha256
                    || recipe.Base.ReleaseTag != "v" + currentVersion))
                {
                    delta = false;
                    reason = "delta-baseline-mismatch";
                    continue;
                }
                if (File.Exists(staging) || Directory.Exists(staging)) throw new IOException("update.staging_exists");
                if (freeze(installation, currentVersion, true) != installed) throw new InvalidDataException("update.baseline_changed");
                artifacts.EnsureDirectory(staging);
                foreach (var file in recipe.IncludedFiles)
                {
                    token.ThrowIfCancellationRequested();
                    using var input = archive.GetEntry(file.Path)!.Open();
                    UpdateDelivery.CopyFile(input, staging, file, artifacts, token);
                }
                foreach (var file in recipe.ReusedFiles)
                {
                    token.ThrowIfCancellationRequested();
                    string path = Path.Combine(installation, file.Path);
                    PayloadPathSafety.RequireLinkFree(path);
                    if (file.Path == UpdateRecipe.UpdateEntry && !File.Exists(path))
                        path = Path.Combine(installation, "NexusPipeline.exe");
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    UpdateDelivery.CopyFile(input, staging, file, artifacts, token);
                }
                var target = new UpdatePayloadIdentity(recipe.PayloadManifestSha256, recipe.BuildId, recipe.FrontendHash, InstallationGeneration.Id);
                if (freeze(staging, release.VersionText, false) != target) throw new InvalidDataException("update.recipe_target_identity");
                var files = recipe.IncludedFiles.Concat(recipe.ReusedFiles).OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
                UpdateDelivery.CreateLocalPackage(zipPath, staging, files, artifacts, token);
                await UpdateAssetDownloader.WriteChecksumAsync(checksumPath, artifacts.FileHash(zipPath), token, artifacts).ConfigureAwait(false);
                if (freeze(staging, release.VersionText, false) != target) throw new InvalidDataException("update.recipe_target_changed");
                long reusedBytes = recipe.ReusedFiles.Sum(file => file.SizeBytes);
                return new(delta ? "delta" : "full", received, reusedBytes, reason, storage,
                    new(delta ? "delta" : "full", asset, Convert.ToHexStringLower(SHA256.HashData(bytes)), installed.ManifestSha256, received, reusedBytes));
            }
            throw new InvalidDataException("update.recipe_fallback_exhausted");
        }
        finally { temporary.Cleanup(); }
    }

    internal static (UpdateRecipe Recipe, byte[] Bytes) ReadVerifiedRecipe(ZipArchive archive, string version, string kind)
    {
        ValidateEntries(archive);
        var entry = archive.GetEntry(UpdateRecipe.RecipePath) ?? throw new InvalidDataException("update.recipe_missing");
        if (entry.Length is < 1 or > UpdateRecipe.MaxBytes) throw new InvalidDataException("update.recipe_size");
        byte[] bytes = new byte[checked((int)entry.Length)];
        using (var input = entry.Open()) input.ReadExactly(bytes);
        var recipe = UpdateRecipe.Parse(bytes, version, kind);
        string[] expected = [.. recipe.IncludedFiles.Select(file => file.Path), .. recipe.BundledFiles.Select(file => file.Path),
            UpdateRecipe.RecipePath, .. UpdateRecipe.Guides];
        if (!archive.Entries.Select(file => file.FullName).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("update.recipe_file_set");
        foreach (var file in recipe.IncludedFiles.Concat(recipe.BundledFiles))
        {
            var included = archive.GetEntry(file.Path)!;
            if (included.Length != file.SizeBytes) throw new InvalidDataException("update.recipe_file_size");
            using var input = included.Open();
            if (Convert.ToHexStringLower(SHA256.HashData(input)) != file.Sha256)
                throw new InvalidDataException("update.recipe_file_digest");
        }
        return (recipe, bytes);
    }

    private static void ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count > UpdatePackage.MaxArchiveEntries + 3) throw new InvalidDataException("update.recipe_entry_count");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            int mode = (entry.ExternalAttributes >> 16) & 0xf000;
            bool metadata = entry.FullName == UpdateRecipe.RecipePath || UpdateRecipe.Guides.Contains(entry.FullName);
            long limit = metadata ? UpdateRecipe.MaxBytes : UpdatePackage.MaxSingleEntryBytes;
            if (!ApplicationPayload.SafePath(entry.FullName) || !names.Add(entry.FullName) || mode is not (0 or 0x8000)
                || entry.Length < 0 || entry.Length > limit || entry.Length > 0
                && (entry.CompressedLength <= 0 || entry.Length / (double)entry.CompressedLength > UpdatePackage.MaxCompressionRatio))
                throw new InvalidDataException("update.recipe_entry");
            total = checked(total + entry.Length);
            if (total > UpdatePackage.MaxExtractedBytes + 3L * UpdateRecipe.MaxBytes) throw new InvalidDataException("update.recipe_archive_budget");
        }
    }

    private sealed class ProgressSink(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => report(value);
    }
}
