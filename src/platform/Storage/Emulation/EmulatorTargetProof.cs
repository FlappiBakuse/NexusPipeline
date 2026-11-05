using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Extensibility.Emulation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Serialization.Protocol;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Emulation;

internal sealed record EmulatorSourceProof(string Format, int Version, EmulatorTargetSelection Selection,
    string InstallationId, string InstanceIdentity, string? ControlName, int? Index, string Directory,
    FileTreePhysicalIdentity Physical, ExecutionPlanInterpreterImage Control, ExecutionPlanInterpreterImage Adb,
    string Endpoint, ExecutionPlanPlanReference Manifest, ExecutionPlanExtensionLocksItem Lock,
    string ActivationId, long InstalledRevision, IReadOnlyList<BlobReference> Observations);

internal static class EmulatorTargetProof
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    internal static string Vendor(EmulatorVendor vendor) => vendor switch
    { EmulatorVendor.LdPlayer => "ldplayer", EmulatorVendor.Nox => "nox", EmulatorVendor.BlueStacks => "bluestacks", _ => throw Invalid() };

    internal static string Generation(EmulatorTarget target, PhysicalObjectIdentity physical) => EmulatorSourceLease.Hash(
        Vendor(target.Installation.Vendor) + "\n" + target.Installation.Id + "\n" + target.Instance.Identity + "\n" +
        target.Instance.Index + "\n" + target.Instance.ControlName + "\n" + EmulatorSourceLease.Identity(physical));

    internal static EmulatorTargetCommand Command(string vendor, string identity, string? name, int? index, bool quit)
    {
        if (vendor == "ldplayer" && index is >= 0)
            return new() { Arguments = [quit ? "quit" : "launch", "--index", index.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)], Detached = false };
        if (vendor == "nox" && (index is >= 0 || name is not null && Selector(name)))
            return new() { Arguments = [quit ? "quit" : "launch", index is { } number ? "-index:" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-name:" + name], Detached = false };
        if (vendor == "bluestacks" && !quit && Selector(identity))
            return new() { Arguments = ["--instance", identity], Detached = true };
        throw Invalid();
    }

    internal static EmulatorTargetFrozen Freeze(EmulatorSourceProof proof, ImmutableBlobStore blobs)
    {
        var saved = blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(proof, Options));
        return new() { Selection = proof.Selection, ExtensionId = "emulator-support", ProviderId = "vendors",
            InstalledRevision = proof.InstalledRevision,
            PackageSha256 = proof.Lock.PackageSha256, ActivationId = proof.ActivationId, InstallationId = proof.InstallationId,
            InstanceDirectory = proof.Directory, InstancePhysicalId = proof.Physical, Control = proof.Control,
            Launch = Command(proof.Selection.Vendor, proof.InstanceIdentity, proof.ControlName, proof.Index, false),
            Quit = proof.Selection.Vendor == "bluestacks" ? null : Command(proof.Selection.Vendor, proof.InstanceIdentity, proof.ControlName, proof.Index, true),
            SourceEvidence = new() { RelativePath = saved.RelativePath, SizeBytes = saved.SizeBytes, Sha256 = saved.Sha256 } };
    }

    internal static IReadOnlyList<BlobReference> Require(EmulatorTargetFrozen frozen, ExecutionTargetAndroid android,
        ImmutableBlobStore blobs, bool current)
    {
        var reference = new BlobReference(frozen.SourceEvidence.RelativePath, frozen.SourceEvidence.SizeBytes, frozen.SourceEvidence.Sha256);
        using var stream = blobs.OpenVerified(reference);
        if (stream.Length > StrictJson.MaximumBytes) throw Invalid();
        var proof = JsonSerializer.Deserialize<EmulatorSourceProof>(stream, Options) ?? throw Invalid();
        if (proof.Format != "nexus.emulator.source" || proof.Version != 1 || frozen.ExtensionId != "emulator-support" || frozen.ProviderId != "vendors" ||
            frozen.Selection != proof.Selection || frozen.InstallationId != proof.InstallationId || frozen.InstanceDirectory != proof.Directory ||
            frozen.InstancePhysicalId != proof.Physical || frozen.ActivationId != proof.ActivationId || frozen.PackageSha256 != proof.Lock.PackageSha256 ||
            frozen.InstalledRevision != proof.InstalledRevision || android.Endpoint != proof.Endpoint || !Same(android.Tool, proof.Adb) ||
            !Same(frozen.Control, proof.Control) || !SameCommand(frozen.Launch, Command(proof.Selection.Vendor, proof.InstanceIdentity, proof.ControlName, proof.Index, false)) ||
            (proof.Selection.Vendor == "bluestacks" ? frozen.Quit is not null : frozen.Quit is null ||
                !SameCommand(frozen.Quit, Command(proof.Selection.Vendor, proof.InstanceIdentity, proof.ControlName, proof.Index, true)))) throw Invalid();
        if (proof.Selection.Generation != EmulatorSourceLease.Hash(proof.Selection.Vendor + "\n" + proof.InstallationId + "\n" + proof.InstanceIdentity + "\n" +
            proof.Index + "\n" + proof.ControlName + "\n" + EmulatorSourceLease.Identity(new(proof.Physical.VolumeSerial, proof.Physical.FileId)))) throw Invalid();
        if (current) { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); RequireCurrent(frozen); }
        return proof.Observations.Prepend(new(proof.Manifest.RelativePath, proof.Manifest.SizeBytes, proof.Manifest.Sha256)).Prepend(reference).ToArray();
    }

    internal static EmulatorSourceProof Read(EmulatorTargetFrozen frozen, ImmutableBlobStore blobs)
    {
        using var input = blobs.OpenVerified(new(frozen.SourceEvidence.RelativePath, frozen.SourceEvidence.SizeBytes, frozen.SourceEvidence.Sha256));
        return JsonSerializer.Deserialize<EmulatorSourceProof>(input, Options) ?? throw Invalid();
    }

    [SupportedOSPlatform("windows")]
    internal static void RequireCurrent(EmulatorTargetFrozen frozen)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(frozen.InstanceDirectory);
        var actual = PhysicalObjectIdentity.Read(path);
        if (actual.VolumeSerial != frozen.InstancePhysicalId.VolumeSerial || actual.FileId != frozen.InstancePhysicalId.FileId) throw new IOException("emulator_instance_generation_changed");
        ExecutionLaunchInputs.RequireImage(frozen.Control);
    }

    internal static bool Same(ExecutionPlanInterpreterImage first, ExecutionPlanInterpreterImage second) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(first), JsonSerializer.SerializeToElement(second));
    private static bool SameCommand(EmulatorTargetCommand first, EmulatorTargetCommand second) => first.Detached == second.Detached && first.Arguments.SequenceEqual(second.Arguments);
    private static bool Selector(string value) => value.Length is > 0 and <= 128 && value.All(character => char.IsLetterOrDigit(character) || character is ' ' or '_' or '-' or '.');
    private static IOException Invalid() => new("emulator_target_source_unbound");
}
