using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Emulation;

internal static class EmulatorPlanBinding
{
    internal static void Require(ExecutionPlan plan, ImmutableBlobStore blobs, ContractReader contracts)
    {
        foreach (var android in plan.Steps.Select(step => step.Target?.Android).OfType<Serialization.Protocol.ExecutionTargetAndroid>())
        {
            if (android.Emulator is not { } emulator) continue;
            var proof = EmulatorTargetProof.Read(emulator, blobs);
            if (plan.ExtensionLocks.SingleOrDefault(value => value.ExtensionId == emulator.ExtensionId) != proof.Lock ||
                proof.Manifest.Sha256 != proof.Lock.ManifestSha256) throw new IOException("emulator_manifest_unbound");
            using var input = blobs.OpenVerified(new(proof.Manifest.RelativePath, proof.Manifest.SizeBytes, proof.Manifest.Sha256));
            if (input.Length > StrictJson.MaximumBytes) throw new IOException("emulator_manifest_unbound");
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            var manifest = new ExtensionManifestAdmission(contracts).ReadCurrent(bytes, emulator.ExtensionId);
            if (!manifest.Capabilities.Contains("emulator.provide", StringComparer.Ordinal) ||
                manifest.Managed?.Emulators?.Contains(emulator.ProviderId, StringComparer.Ordinal) != true ||
                proof.Lock.PackageVersion != manifest.PackageVersion || proof.Lock.SdkVersion != manifest.SdkVersion ||
                proof.Lock.ContractFingerprint != manifest.ContractFingerprint) throw new IOException("emulator_manifest_unbound");
        }
    }
}
