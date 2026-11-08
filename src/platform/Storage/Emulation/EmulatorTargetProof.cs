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
    string ActivationId, long InstalledRevision, IReadOnlyList<BlobReference> Observations)
{
    public string? ProviderId { get; init; }
    public EmulatorTargetCommand? Launch { get; init; }
    public EmulatorTargetCommand? Quit { get; init; }
}

internal static class EmulatorTargetProof
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    internal static string Vendor(string vendor) => vendor.ToLowerInvariant();

    internal static string Generation(EmulatorTarget target, PhysicalObjectIdentity physical) => EmulatorSourceLease.Hash(
        Vendor(target.Installation.Vendor) + "\n" + target.Installation.Id + "\n" + target.Instance.Identity + "\n" +
        target.Instance.Index + "\n" + target.Instance.ControlName + "\n" + EmulatorSourceLease.Identity(physical));

    internal static EmulatorTargetCommand Command(EmulatorCommand command, EmulatorTarget target)
    {
        if (command.ToolId != target.Installation.Control.Id || command.Arguments.Count is < 1 or > 8 ||
            command.Arguments.Any(value => value.Length is < 1 or > 512 || value.Any(char.IsControl))) throw Invalid();
        return new() { Arguments = command.Arguments.ToArray(), Detached = command.Detached };
    }

    internal static EmulatorTargetFrozen Freeze(EmulatorSourceProof proof, ImmutableBlobStore blobs)
    {
        var saved = blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(proof, Options));
        return new() { Selection = proof.Selection, ExtensionId = proof.Lock.ExtensionId, ProviderId = proof.ProviderId ?? throw Invalid(),
            InstalledRevision = proof.InstalledRevision,
            PackageSha256 = proof.Lock.PackageSha256, ActivationId = proof.ActivationId, InstallationId = proof.InstallationId,
            InstanceDirectory = proof.Directory, InstancePhysicalId = proof.Physical, Control = proof.Control,
            Launch = proof.Launch ?? throw Invalid(), Quit = proof.Quit,
            SourceEvidence = new() { RelativePath = saved.RelativePath, SizeBytes = saved.SizeBytes, Sha256 = saved.Sha256 } };
    }

    internal static IReadOnlyList<BlobReference> Require(EmulatorTargetFrozen frozen, ExecutionTargetAndroid android,
        ImmutableBlobStore blobs, bool current)
    {
        var reference = new BlobReference(frozen.SourceEvidence.RelativePath, frozen.SourceEvidence.SizeBytes, frozen.SourceEvidence.Sha256);
        using var stream = blobs.OpenVerified(reference);
        if (stream.Length > StrictJson.MaximumBytes) throw Invalid();
        var proof = JsonSerializer.Deserialize<EmulatorSourceProof>(stream, Options) ?? throw Invalid();
        if (proof.Format != "nexus.emulator.source" || proof.Version is not (1 or 2) || frozen.ExtensionId != proof.Lock.ExtensionId ||
            proof.Version == 2 && frozen.ProviderId != proof.ProviderId ||
            frozen.Selection != proof.Selection || frozen.InstallationId != proof.InstallationId || frozen.InstanceDirectory != proof.Directory ||
            frozen.InstancePhysicalId != proof.Physical || frozen.ActivationId != proof.ActivationId || frozen.PackageSha256 != proof.Lock.PackageSha256 ||
            frozen.InstalledRevision != proof.InstalledRevision || android.Endpoint != proof.Endpoint || !Same(android.Tool, proof.Adb) ||
            !Same(frozen.Control, proof.Control) || proof.Version == 2 &&
            (proof.Launch is null || !SameCommand(frozen.Launch, proof.Launch) ||
                (proof.Quit is null ? frozen.Quit is not null : frozen.Quit is null || !SameCommand(frozen.Quit, proof.Quit)))) throw Invalid();
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
    internal static bool SameCommand(EmulatorTargetCommand first, EmulatorTargetCommand second) => first.Detached == second.Detached && first.Arguments.SequenceEqual(second.Arguments);
    private static IOException Invalid() => new("emulator_target_source_unbound");
}
