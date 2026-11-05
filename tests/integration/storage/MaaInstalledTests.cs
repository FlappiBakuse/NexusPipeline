using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class MaaInstalledTests
{
    [Fact(DisplayName = "The Original Maa ZIP Activates Twice Without Loading Native Libraries In Host And Removes After Draining Its Compiler And Worker Registration")]
    public async Task DrainsInstalledPackage()
    {
        using var inputs = new SpecializedPackageInputs("maa-framework", "NEXUS_FORMAL_MAA_PACKAGE_RECEIPT", "managed-code");
        var providers = new ExecutionProviderRegistry();
        using var fixture = new InstallRuntimeFixture(originalManifest: inputs.Manifest, originalManifestBytes: inputs.ManifestBytes,
            packageArchive: inputs.Archive, providers: providers);
        var owner = fixture.Package.Catalog;
        RequireUnmapped();
        var first = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        Assert.True(first.IsEnabled);
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose();
        Guid? activation = null;
        for (var cycle = 0; cycle < 2; cycle++)
        {
            if (cycle > 0)
            {
                var next = await new InstalledActivationService(owner.Instance, owner.Database, owner.Lease,
                    owner.Contracts, fixture.Roots, owner.Resources, owner.Index, owner.Store, fixture.Activator)
                    .ActivateAsync("maa-framework", default);
                Assert.NotEqual(activation, next.ActivationId);
            }
            var published = Assert.Single(fixture.Activator.ReadPublishedScopes());
            activation = published.ActivationId;
            Assert.Equal("maa-framework", published.ExtensionId);
            using (providers.Acquire("maa-framework")) { }
            var compiler = await fixture.Activator.Compilers.UseAsync("maa-framework", "project", (value, current) =>
            {
                current(); return ValueTask.FromResult(value.GetType().Name);
            }, default);
            Assert.NotEmpty(compiler);
            RequireUnmapped();
            using var mutation = owner.Resources.TryAcquire(Guid.NewGuid(), [new("extension:maa-framework", ResourceClaimMode.Mutation)]).Lease;
            Assert.NotNull(mutation);
            using (var stop = await fixture.Activator.ReserveForApplyAsync(mutation, "maa-framework", default))
                stop.RequireNativeUnmapped();
            Assert.Empty(fixture.Activator.ReadPublishedScopes());
            Assert.Equal("execution_extension_provider_unavailable", Assert.Throws<IOException>(() => providers.Acquire("maa-framework")).Message);
            await Assert.ThrowsAsync<IOException>(async () => await fixture.Activator.Compilers.UseAsync("maa-framework", "project",
                (_, _) => ValueTask.FromResult(true), default));
            RequireUnmapped(); inputs.VerifyUnchanged();
        }
        await RemoveAsync(fixture);
        RequireUnmapped(); inputs.VerifyUnchanged();
    }

    private static async Task RemoveAsync(InstallRuntimeFixture fixture)
    {
        var owner = fixture.Package.Catalog; var operation = Guid.NewGuid();
        var claims = PhysicalResourceAdmission.Merge(new ResourceClaim[]
        {
            new("extension:maa-framework", ResourceClaimMode.Mutation), new("blobs:content", ResourceClaimMode.Reference),
            new("settings:host", ResourceClaimMode.Reference)
        }.Concat(PhysicalResourceAdmission.Read(fixture.Target)).Concat(PhysicalResourceAdmission.Read(
            ExtensionWorkspacePath.For(fixture.Target, operation))));
        using var root = owner.Resources.TryAcquire(operation, claims).Lease;
        Assert.NotNull(root);
        var installed = new ExtensionInstalledEvidence(owner.Instance, owner.Database, owner.Contracts, fixture.Roots).ReadCurrent("maa-framework")!;
        using var prepared = PreparedExtensionPayload.Prepare(owner.Instance, owner.Database, owner.Lease, owner.Resources,
            owner.Index, root, PayloadAction.Remove, "maa-framework", installed.Metadata.Revision,
            new HostSettingsStore(owner.Database).Read().Revision, null, fixture.Roots, owner.Contracts, "local", default);
        _ = new PayloadApplyAcceptance(owner.Instance, owner.Contracts, owner.Database, fixture.Roots).AcceptPrepared(prepared, DateTimeOffset.UtcNow);
        _ = await fixture.Session().ApplyAsync(prepared, default);
        var removed = new ExtensionInstalledEvidence(owner.Instance, owner.Database, owner.Contracts, fixture.Roots).ReadCurrent("maa-framework");
        Assert.NotNull(removed); Assert.False(removed.Metadata.IsInstalled);
        Assert.Equal(FileObjectKind.Missing, removed.Footprint.Payload.Snapshot.Shape);
        Assert.False(Directory.Exists(fixture.Target));
    }

    private static void RequireUnmapped()
    {
        using var process = Process.GetCurrentProcess();
        Assert.DoesNotContain(process.Modules.Cast<ProcessModule>(), module => module.ModuleName is
            "MaaFramework.dll" or "MaaCustomControlUnit.dll" or "MaaAgentClient.dll" or "MaaAgentServer.dll" or "MaaPluginDemo.dll");
    }
}
