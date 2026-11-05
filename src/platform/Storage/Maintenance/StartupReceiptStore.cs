using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class StartupReceiptStore(RuntimeInstance instance, StateDatabase database,
    ContractReader contracts, PayloadApplyRoots roots, UpdateFeedVerifier verifier)
{
    internal BlobReference Prepare(InstanceLease lease, MaintenanceStartupChallenge challenge, string startupJobName,
        CancellationToken cancellation)
    {
        MaintenanceStartupFrames.Require(challenge);
        lease.VerifyHeld(instance);
        using var inputs = MaintenanceStartupInputs.Hold(instance, database, contracts, roots, verifier, challenge.Binding, cancellation);
        var process = ProcessIdentityReader.Read(Environment.ProcessId);
        RequireHost(inputs, process);
        RequireStartupJob(startupJobName, process.ProcessId);
        var document = Build(inputs, challenge, process, lease.StartupGeneration, startupJobName);
        var bytes = BoundedJson.Serialize(document);
        using var shape = contracts.Read(bytes, "update-receipt.schema.json");
        inputs.RequireCurrent(database, challenge.Binding); lease.VerifyHeld(instance);
        return new ImmutableBlobStore(instance, contracts).PersistPlan(bytes);
    }

    internal StartupReceiptLease ReadPrepared(MaintenanceStartupChallenge challenge, MaintenanceStartupReply reply,
        ProcessIdentity actualHost, string actualJobName, CancellationToken cancellation)
    {
        MaintenanceStartupFrames.Require(challenge);
        if (reply.Binding != challenge.Binding || reply.Nonce != challenge.Nonce) throw Invalid();
        var inputs = MaintenanceStartupInputs.Hold(instance, database, contracts, roots, verifier, challenge.Binding, cancellation);
        FileStream? held = null;
        try
        {
            RequireHost(inputs, actualHost);
            held = new ImmutableBlobStore(instance, contracts).OpenVerified(reply.Receipt);
            var bytes = new byte[checked((int)held.Length)]; held.ReadExactly(bytes);
            using var document = contracts.Read(bytes, "update-receipt.schema.json");
            var expected = Build(inputs, challenge, actualHost, reply.StartupGeneration, actualJobName);
            expected["recordedAt"] = document.RootElement.GetProperty("recordedAt").GetString();
            if (!JsonElement.DeepEquals(document.RootElement, JsonSerializer.SerializeToElement(expected))) throw Invalid();
            inputs.RequireCurrent(database, challenge.Binding);
            return new(inputs, held, reply.Receipt, reply.StartupGeneration, actualHost, actualJobName);
        }
        catch { held?.Dispose(); inputs.Dispose(); throw; }
    }

    private static JsonObject Build(MaintenanceStartupInputs inputs, MaintenanceStartupChallenge challenge,
        ProcessIdentity host, Guid generation, string jobName)
    {
        MaintenanceStartupFrames.Require(challenge.Binding);
        if (generation == Guid.Empty || challenge.Nonce is not { Length: 64 } ||
            challenge.Nonce.Any(c => !(c is >= 'a' and <= 'f' or >= '0' and <= '9'))) throw Invalid();
        var binding = inputs.Manifest.StoreBinding;
        return new()
        {
            ["format"] = "nexus.update.receipt", ["version"] = 1,
            ["instanceId"] = challenge.Binding.InstanceId, ["operationId"] = challenge.Binding.OperationId,
            ["journalId"] = challenge.Binding.JournalId, ["correlationId"] = challenge.Binding.HandoffId,
            ["stage"] = inputs.Restored is null ? "candidate-prepared" : "old-ready", ["payloadManifestSha256"] = inputs.Source.PayloadManifest.Sha256,
            ["planSha256"] = challenge.Binding.Plan.Sha256,
            ["oldPayloadSha256"] = TreeDigest(inputs.Plan.Before.Snapshot),
            ["candidatePayloadSha256"] = TreeDigest(inputs.Plan.Candidate.Snapshot),
            ["storeBinding"] = new JsonObject
            {
                ["channel"] = binding.Channel == ExtensionStoreChannel.Stable ? "stable" : "develop",
                ["generationId"] = binding.GenerationId, ["sequence"] = binding.Sequence, ["catalogUrl"] = binding.CatalogUrl,
                ["catalogSha256"] = binding.CatalogSha256, ["signatureUrl"] = binding.SignatureUrl
            },
            ["process"] = new JsonObject
            {
                ["processId"] = host.ProcessId, ["createdFileTime"] = host.CreatedAtUtc.ToFileTimeUtc(),
                ["imagePath"] = host.ImagePath, ["imageSha256"] = host.ImageSha256, ["userSid"] = host.UserSid, ["isElevated"] = host.IsElevated
            },
            ["startup"] = new JsonObject
            {
                ["handoffId"] = challenge.Binding.HandoffId, ["generationId"] = generation, ["jobName"] = jobName,
                ["nonce"] = challenge.Nonce, ["installedRoot"] = new JsonObject
                    { ["volumeSerial"] = inputs.InstalledRoot.VolumeSerial, ["fileId"] = inputs.InstalledRoot.FileId }
            },
            ["recordedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"),
            ["detailCode"] = inputs.Restored is null ? "candidate_startup_prepared" : "original_startup_prepared"
        };
    }

    private static void RequireHost(MaintenanceStartupInputs inputs, ProcessIdentity host)
    {
        if (inputs.Restored is { } restored)
        {
            restored.RequireHost(host);
            using var original = new FileStream(host.ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            UpdateBuildProfile.RequireBundle(original, "NexusPipeline.Host.dll", inputs.Manifest.Profile);
            return;
        }
        var expected = inputs.Manifest.Files.Single(file => file.RelativePath == inputs.Manifest.HostEntry);
        var image = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(inputs.Plan.TargetPath, expected.RelativePath));
        if (!StringComparer.OrdinalIgnoreCase.Equals(host.ImagePath, image) || host.ImageSha256 != expected.Sha256) throw Invalid();
        using var compiled = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read);
        UpdateBuildProfile.RequireBundle(compiled, "NexusPipeline.Host.dll", inputs.Manifest.Profile);
    }

    private static void RequireStartupJob(string jobName, int processId)
    {
        if (!jobName.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) || jobName.Length != 50 ||
            !Guid.TryParseExact(jobName[18..], "N", out _)) throw Invalid();
        using var job = JobNative.OpenJobObjectW(4, false, jobName);
        using var process = JobNative.OpenProcess(0x101000, false, processId);
        if (job.IsInvalid || process.IsInvalid || !JobNative.IsProcessInJob(process, job, out var member) || !member ||
            JobStateReader.Limits(job).Basic.Flags != 0x2800) throw Invalid();
    }

    internal static string TreeDigest(NexusPipeline.Core.Configuration.FileTreeSnapshot tree) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(FileTreeProtocol.ToWire(tree))));
    private static IOException Invalid() => new("maintenance_startup_receipt_unbound");
}
