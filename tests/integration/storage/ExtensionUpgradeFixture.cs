using System.Runtime.Versioning;
using NexusPipeline.Core.Extensions;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ExtensionUpgradeFixture : IAsyncDisposable
{
    internal readonly InstallRuntimeFixture Original;
    internal readonly BoundPackageFixture Next;
    internal readonly string NextActivationDirectory;
    internal ResourceOperationLease? Root;
    internal VerifiedExtensionPackage? Verified;
    internal PreparedExtensionPayload? Prepared;
    internal PayloadAcceptanceResult? Accepted;

    private ExtensionUpgradeFixture(InstallRuntimeFixture original, BoundPackageFixture next, string activation)
    { Original = original; Next = next; NextActivationDirectory = activation; }

    internal static async Task<ExtensionUpgradeFixture> CreateAsync(bool enabled, bool sameVersion = false, int originalPayloadFiles = 0, string nextMode = "return",
        ExtensionStoreChannel originalChannel = ExtensionStoreChannel.Stable, ExtensionStoreChannel nextChannel = ExtensionStoreChannel.Stable)
    {
        var original = new InstallRuntimeFixture(savedEnabled: enabled, managedMode: "return", payloadFiles: originalPayloadFiles, channel: originalChannel);
        try
        {
            _ = await original.Session().ApplyAsync(original.Prepared, default);
            original.Prepared.Dispose(); original.Verified.Dispose(); original.Root.Dispose();
            var activation = Path.Combine(original.ActivationDirectory, "next"); Directory.CreateDirectory(activation);
            var version = sameVersion ? "0.5.0" : "0.6.0";
            var next = new BoundPackageFixture("managed-code", manifest =>
            {
                manifest["packageVersion"] = version; manifest["managed"]!["entryType"] = "Controlled.ManagedExtension";
            }, directory =>
            {
                var image = Environment.GetEnvironmentVariable("NEXUS_MANAGED_EXTENSION_FIXTURE_DLL") ?? throw new IOException("fixture_dll_required");
                File.Copy(image, Path.Combine(directory, "entry.dll"), overwrite: true);
                File.WriteAllBytes(Path.Combine(directory, "activation-input.json"), JsonSerializer.SerializeToUtf8Bytes(new
                { Directory = activation, Nonce = Guid.NewGuid().ToString("N"), Mode = nextMode }));
            }, original.Package.Catalog, channel: nextChannel);
            next.Publish(2, entry => entry["packageVersion"] = version);
            Refresh(original);
            return new(original, next, activation);
        }
        catch { original.Dispose(); throw; }
    }

    internal void Prepare(PayloadAction action)
    {
        var owner = Original.Package.Catalog;
        var claims = PhysicalResourceAdmission.Merge(new ResourceClaim[]
        {
            new("extension:fixture-plugin", ResourceClaimMode.Mutation),
            new("extensions:catalog", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference),
            new("settings:host", ResourceClaimMode.Reference)
        }.Concat(PhysicalResourceAdmission.Read(Original.Target)).Concat(PhysicalResourceAdmission.Read(
            ExtensionWorkspacePath.For(Original.Target, Next.OperationId))));
        Root = owner.Resources.TryAcquire(Next.OperationId, claims).Lease ?? throw new IOException("fixture_root_unavailable");
        VerifiedExtensionPackage.Candidate? candidate = null;
        if (action != PayloadAction.Remove)
        {
            Verified = Next.Prepare(Root); candidate = Verified.CaptureCandidate(Root, Next.Blobs, default);
        }
        Prepared = PreparedExtensionPayload.Prepare(owner.Instance, owner.Database, owner.Lease, owner.Resources,
            owner.Index, Root, action, "fixture-plugin", 1, new HostSettingsStore(owner.Database).Read().Revision,
            candidate, Original.Roots, owner.Contracts, "local", default);
    }

    internal PayloadAcceptanceResult Accept()
    {
        var owner = Original.Package.Catalog;
        return Accepted = new PayloadApplyAcceptance(owner.Instance, owner.Contracts, owner.Database, Original.Roots)
            .AcceptPrepared(Prepared!, DateTimeOffset.UtcNow);
    }

    internal PayloadApplySession Session(IPayloadPersistenceObserver? observer = null) => Original.Session(observer);
    internal BoundExtensionPayload Bound() => Original.Binding().Read(Accepted!.JournalId, Accepted.Plan);
    internal string Backup(BoundExtensionPayload bound) => Path.Combine(ExtensionWorkspacePath.For(bound.Plan), "before");
    internal FileTreeSnapshot Scan(string path) => new FileTreeScanner(new(4096, 268435456)).Capture(path, default);

    private static void Refresh(InstallRuntimeFixture fixture)
    {
        var owner = fixture.Package.Catalog;
        new RecoveryCoordinator(new RecoveryJournalRegistry(owner.Database),
            new AdmissionPublicationEvidence(owner.Instance, owner.Database, owner.Contracts, fixture.Roots), owner.Index)
            .VerifyAndPublish();
        ConfigurationRecoveryHolds.Publish(owner.Database, owner.Index, fixture.Roots);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Original.Activator.StopAllAsync(TimeSpan.FromSeconds(5)); }
        finally { Prepared?.Dispose(); Verified?.Dispose(); Root?.Dispose(); Original.Dispose(); }
    }
}
