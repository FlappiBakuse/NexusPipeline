using System.Runtime.Versioning;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class LocalExtensionIntakeTests
{
    [Fact(DisplayName = "A New Signed Local Version Refreshes A Stale Catalog And Requires Replayable Confirmation")]
    public async Task ConfirmsSignedUpdate()
    {
        using var owner = new ExtensionCatalogFixture();
        var extensions = Path.Combine(owner.Instance.RuntimeRoot, "extensions");
        var roots = new PayloadApplyRoots(Path.Combine(owner.Instance.RuntimeRoot, "host-payload"), extensions);
        Directory.CreateDirectory(extensions);
        using var next = new BoundPackageFixture("data-specialized",
            manifest => manifest["packageVersion"] = "0.6.0", catalog: owner);
        var trust = new ExtensionReleaseTrust(new Dictionary<string, byte[]> { ["fixture-only"] = owner.Signer.ExportSubjectPublicKeyInfo() },
            ExtensionCatalogFixture.MetadataRoot, [ExtensionCatalogFixture.Artifacts], 1, 1);
        await using var runtime = new ExtensionRuntimeLifecycle(owner.Instance, owner.Database, owner.Lease,
            owner.Contracts, roots, owner.Resources, owner.Index, new ExecutionProviderRegistry(), trust);
        runtime.ConfigurePorts(new(null, null, null, null, null, null));
        var original = new InstallRuntimeFixture(savedEnabled: false, catalog: owner,
            payloadRoots: roots, existingActivator: runtime.Activator);
        try { await original.Session().ApplyAsync(original.Prepared, default); }
        finally { original.Prepared.Dispose(); original.Verified.Dispose(); original.Root.Dispose(); }

        var dropped = Path.Combine(extensions, "new-version.zip");
        File.Copy(next.Archive, dropped);
        var invalidBundle = Directory.CreateDirectory(Path.Combine(extensions, "malformed-bundle")).FullName;
        File.WriteAllText(Path.Combine(invalidBundle, "index.json"), """{"channel":0}""");
        File.WriteAllText(Path.Combine(invalidBundle, "catalog.json"), "{}");
        File.WriteAllText(Path.Combine(invalidBundle, "catalog.sig.json"), "{}");
        using var handler = new ExtensionWorkflowFixture.TransferHandler([], false);
        using var transport = new StoreHttpTransport(owner.Contracts, trust.Policy, () => new HttpClient(handler, disposeHandler: false));
        var workflow = new ExtensionApplyWorkflow(owner.Instance, owner.Database, owner.Lease, owner.Contracts, roots,
            owner.Resources, owner.Index, runtime.Catalog, runtime.Activator, transport);
        using var mutation = new SemaphoreSlim(1);
        var refreshes = 0;
        var intake = new LocalExtensionIntake(owner.Instance, owner.Database, owner.Contracts, roots, runtime, workflow,
            (_, channel, _) =>
            {
                Assert.Equal(ExtensionStoreChannel.Stable, channel);
                next.Publish(2, entry => entry["packageVersion"] = "0.6.0");
                refreshes++;
                var saved = owner.Store.ReadCached(channel)!;
                return Task.FromResult(new ExtensionCatalogView(saved.Identity, saved.PublishedAt, saved.Entries));
            }, (_, _) => throw new InvalidOperationException("unexpected bundle"), mutation, default);

        var snapshot = await intake.ScanAsync();
        Assert.Equal(1, refreshes);
        Assert.Contains(snapshot.Items, item => item.Reason == "extension_local_package_invalid");
        var candidate = Assert.Single(snapshot.Items, item => item.ExtensionId == "fixture-plugin");
        Assert.Equal("pendingUpdate", candidate.State);
        Assert.Equal("0.5.0", State().PackageVersion);
        Assert.True(File.Exists(dropped));

        var operation = Guid.NewGuid();
        var applied = await intake.EnableAsync(candidate.CandidateId, operation, "local");
        Assert.Equal("installed", applied.Status);
        Assert.Equal(applied, await intake.EnableAsync(candidate.CandidateId, operation, "local"));
        Assert.False(State().DesiredEnabled);
        Assert.False(State().IsUnofficial);
        Assert.Equal("0.6.0", State().PackageVersion);
        Assert.False(File.Exists(dropped));
        Assert.Equal(0, handler.Requests);
        await intake.WaitStoppedAsync();

        ExtensionRuntimeState State() => Assert.Single(runtime.ReadStates(), item => item.ExtensionId == "fixture-plugin");
    }
}
