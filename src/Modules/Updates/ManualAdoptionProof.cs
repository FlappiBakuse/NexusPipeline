using NexusPipeline.Shared.Versioning;
using System.IO.Compression;
using System.Security.Cryptography;

namespace NexusPipeline.Modules.Updates;

internal sealed record VerifiedManualAdoption(UpdatePayloadIdentity Payload, UpdateReleaseEvidence Release,
    UpdateTransportReceipt Transport, UpdateRecipeBase Base, string CoverRecipeSha256);

internal static class ManualAdoptionProof
{
    internal static async Task<VerifiedManualAdoption> VerifyAsync(HttpClient http, string installation,
        InstallerInstanceIdentity owner, string targetVersion, string workspace, CancellationToken token)
    {
        string previousVersion = owner.Version;
        if (!NexusVersion.TryParse(previousVersion, out var previous)
            || !NexusVersion.TryParse(targetVersion, out var target)
            || previous.CompareTo(NexusVersion.Stable(0, 17, 3)) < 0 || target.CompareTo(previous) <= 0)
            throw new InvalidDataException("installer.adoption_route_unavailable");
        var installed = ApplicationPayload.FreezeForUpdate(installation, targetVersion, installed: true);
        var release = await UpdateDiscovery.FetchOfficialTargetAsync(http, targetVersion, previous, token).ConfigureAwait(false);
        var policy = new UpdateSourcePolicy(null);
        var barrier = await UpdatePolicy.FetchAsync(policy, http, "NexusPipeline-adoption/" + targetVersion,
            Path.Combine(installation, ".nxp", "state"), token).ConfigureAwait(false);
        if (!barrier.Verified || UpdatePolicy.FindBarrier(barrier.Policy!, previous, target) is not null)
            throw new IOException("installer.adoption_policy_unavailable");
        if (File.Exists(workspace) || Directory.Exists(workspace)) throw new IOException("installer.adoption_workspace_exists");
        var artifacts = new UpdateCreatedArtifacts(workspace);
        try
        {
            UpdateStoragePlan.ForInstallation(installation, release.DeltaAsset!.SizeBytes,
                release.ZipAsset.SizeBytes, UpdatePackage.MaxExtractedBytes).RequireAvailable(installation);
            artifacts.EnsureDirectory(workspace);
            string cover = Path.Combine(workspace, "cover.zip");
            await UpdateAssetDownloader.DownloadAsync(http, policy, release.DeltaAsset!, cover, null, token, artifacts).ConfigureAwait(false);
            UpdateRecipe coverRecipe;
            byte[] coverBytes;
            using (var archive = ZipFile.OpenRead(cover))
                (coverRecipe, coverBytes) = UpdateRecipeDelivery.ReadVerifiedRecipe(archive, targetVersion, "update");
            var previousManifest = owner.PayloadFiles.SingleOrDefault(file => file.Path == ApplicationPayload.ManifestPath)?.Sha256;
            if (!coverRecipe.ManualCoverAllowed || coverRecipe.Base?.ReleaseTag != "v" + previousVersion
                || coverRecipe.Base.PayloadManifestSha256 != previousManifest || coverRecipe.PayloadManifestSha256 != installed.ManifestSha256)
                throw new InvalidDataException("installer.adoption_cover_not_authorized");
            var result = await UpdateRecipeDelivery.PrepareAsync(http, policy, release, installation, targetVersion,
                Path.Combine(workspace, "official.zip"), Path.Combine(workspace, "official.sha256"),
                Path.Combine(workspace, "application"), ApplicationPayload.FreezeForUpdate, artifacts, null, token).ConfigureAwait(false);
            var verified = ApplicationPayload.Freeze(Path.Combine(workspace, "application"), targetVersion);
            if (result.Transport != "full" || verified != installed
                || ApplicationPayload.FreezeForUpdate(installation, targetVersion, installed: true) != installed)
                throw new InvalidDataException("installer.adoption_payload_mismatch");
            var evidence = UpdateReleaseEvidence.Freeze(release, installed)
                ?? throw new InvalidDataException("installer.adoption_release_missing");
            var receipt = result.Receipt ?? throw new InvalidDataException("installer.adoption_receipt_missing");
            receipt.Validate(evidence);
            foreach (string optional in new[] { "README.md", UpdateRecipe.UpdateEntry })
            {
                string destination = Path.Combine(installation, optional);
                InstallationOwnership.RequireLinkFree(destination);
                if (File.Exists(destination)) continue;
                token.ThrowIfCancellationRequested();
                if (Directory.Exists(destination)
                    || !string.Equals(Path.GetPathRoot(Path.GetFullPath(workspace)), Path.GetPathRoot(Path.GetFullPath(installation)), StringComparison.OrdinalIgnoreCase)
                    || owner.State != "manual-adoption-pending" || OwnershipChanged()) throw new IOException("installer.adoption_repair_conflict");
                // Publish only independently verified bytes; a crash leaves maintenance suspended until adoption commits.
                File.Move(Path.Combine(workspace, "application", optional), destination, false);
            }
            if (ApplicationPayload.Freeze(installation, targetVersion, installed: true) != installed)
                throw new InvalidDataException("installer.adoption_payload_mismatch");
            return new(installed, evidence, receipt,
                coverRecipe.Base, Convert.ToHexStringLower(SHA256.HashData(coverBytes)));

            bool OwnershipChanged() => System.Text.Json.JsonSerializer.Serialize(InstallationOwnership.Read(installation))
                != System.Text.Json.JsonSerializer.Serialize(owner);
        }
        finally { artifacts.Cleanup(); }
    }
}
