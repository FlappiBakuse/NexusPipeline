using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Emulation;

internal static class EmulatorPlanBinding
{
    internal static void Require(ExecutionPlan plan, ImmutableBlobStore blobs, ContractReader contracts, List<IDisposable> held)
    {
        Dictionary<(BlobReference Reference, string ExtensionId), ExtensionManifest>? manifests = null;
        foreach (var android in plan.Steps.Select(step => step.Target?.Android).OfType<Serialization.Protocol.ExecutionTargetAndroid>())
        {
            if (android.Emulator is not { } emulator) continue;
            var proof = EmulatorTargetProof.Read(emulator, blobs);
            if (plan.ExtensionLocks.SingleOrDefault(value => value.ExtensionId == emulator.ExtensionId) != proof.Lock ||
                proof.Manifest.Sha256 != proof.Lock.ManifestSha256) throw new IOException("emulator_manifest_unbound");
            var reference = new BlobReference(proof.Manifest.RelativePath, proof.Manifest.SizeBytes, proof.Manifest.Sha256);
            var key = (reference, emulator.ExtensionId);
            manifests ??= [];
            if (!manifests.TryGetValue(key, out var manifest))
            {
                var input = blobs.OpenVerified(reference);
                held.Add(input);
                if (input.Length > StrictJson.MaximumBytes) throw new IOException("emulator_manifest_unbound");
                var bytes = new byte[checked((int)input.Length)];
                input.ReadExactly(bytes);
                manifest = new ExtensionManifestAdmission(contracts).ReadCurrent(bytes, emulator.ExtensionId);
                manifests.Add(key, manifest);
            }
            if (!manifest.Capabilities.Contains("emulator.provide", StringComparer.Ordinal) ||
                manifest.Managed?.Emulators?.Contains(emulator.ProviderId, StringComparer.Ordinal) != true ||
                proof.Lock.PackageVersion != manifest.PackageVersion || proof.Lock.SdkVersion != manifest.SdkVersion ||
                proof.Lock.ContractFingerprint != manifest.ContractFingerprint) throw new IOException("emulator_manifest_unbound");
        }
    }
}
