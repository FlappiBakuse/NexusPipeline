using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
[Collection("SerialNative")]
public sealed class OwnedDownloadTests
{
    [Fact(DisplayName = "A Killed Download Owner Leaves One Verified Published Zip That The Real Apply Workflow Reuses Without Transfer")]
    public async Task ReusesInterruptedAdoption()
    {
        using var fixture = new BoundPackageFixture("data-specialized"); fixture.Publish();
        var instance = fixture.Catalog.Instance; var contracts = fixture.Catalog.Contracts;
        var roots = new PayloadApplyRoots(Path.Combine(fixture.Root, "host"), Path.Combine(fixture.Root, "installed"));
        Directory.CreateDirectory(roots.ExtensionPayloadRoot);
        var input = Path.Combine(fixture.Root, "download-input.json"); var report = Path.Combine(fixture.Root, "download-report.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Instance = instance, fixture.Archive, PublicKey = fixture.Catalog.Signer.ExportSubjectPublicKeyInfo(),
            InitialOperationId = fixture.OperationId, RemoveOperationId = Guid.NewGuid(), Roots = roots,
            Point = "download-adopted", Report = report
        }));
        fixture.Catalog.Lease.Dispose();
        using var process = new OwnedCrashProcess();
        var owner = process.Start(["extension-work-recovery", input], fixture.Root, instance.InstanceId,
            fixture.OperationId, Path.Combine(fixture.Root, "download-owner.json"));
        var observed = await process.ReadReportAsync(report, TimeSpan.FromSeconds(60));
        Assert.Equal(owner.ProcessId, observed.GetProperty("processId").GetInt32());
        Assert.True((await process.StopAsync()).CompleteJobStopped); _ = await process.AwaitExitAsync();
        var path = observed.GetProperty("path").GetString()!;
        Assert.Equal(observed.GetProperty("identity").Deserialize<PhysicalObjectIdentity>(), PhysicalObjectIdentity.Read(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(observed.GetProperty("staging").GetString()!)));
        using var lease = InstanceLease.Acquire(instance, contracts);
        var database = StateDatabase.OpenExisting(instance, Path.Combine(fixture.Root, "inspection"), new JournalStartupAdmission(instance, contracts, roots));
        var index = new StartupJournalRecovery(instance, contracts, database, roots).Recover(lease);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        var catalog = new ExtensionCatalogStore(instance, database, lease, resources, contracts, fixture.Catalog.Verifier);
        var activator = new ExtensionActivator(contracts, lease, resources);
        _ = new InstalledPortFactory(instance, database, lease, resources, activator, contracts, index, new(null, null, null, null, null, null));
        using var handler = new ExtensionWorkflowFixture.TransferHandler(File.ReadAllBytes(fixture.Archive), false);
        using var transport = new StoreHttpTransport(contracts, new(new Dictionary<string, byte[]> { ["fixture-only"] = fixture.Catalog.Signer.ExportSubjectPublicKeyInfo() },
            ExtensionCatalogFixture.MetadataRoot, [ExtensionCatalogFixture.Artifacts], 1, 1, new(0, 18, 0), "0.1.0"), () => new HttpClient(handler, false));
        var saved = catalog.ReadCached(ExtensionStoreChannel.Stable)!;
        var request = new ExtensionApplyRequest(PayloadAction.Install, "fixture-plugin", 0, new HostSettingsStore(database).Read().Revision,
            saved.Identity, saved.Entries.Single());
        var result = await new ExtensionApplyWorkflow(instance, database, lease, contracts, roots, resources, index, catalog, activator, transport)
            .RunAsync(Guid.NewGuid(), "local", request, _ => { }, default);
        Assert.True(result.Result!.IsInstalled); Assert.Equal(0, handler.Requests);
        await activator.StopAllAsync(TimeSpan.FromSeconds(5));
    }

    [Theory(DisplayName = "An Owned Verified Download Publishes One Same Volume Object Or Reuses The Existing Exact Content")]
    [InlineData(false)] [InlineData(true)]
    public async Task AdoptsSealedArchive(bool existing)
    {
        using var fixture = new BoundPackageFixture(); fixture.Publish();
        var bytes = File.ReadAllBytes(fixture.Archive);
        var entry = fixture.Catalog.Store.ReadCached(ExtensionStoreChannel.Stable)!.Entries.Single();
        var blobs = fixture.Blobs;
        var reference = new BlobReference($"blobs/content/{entry.ArtifactSha256}.bin", entry.ArtifactBytes, entry.ArtifactSha256);
        if (existing) blobs.PersistContent(fixture.Archive,
            new(Path.GetFileName(fixture.Archive), FileObjectKind.File, entry.ArtifactBytes, entry.ArtifactSha256), default);
        using var download = await OwnedExtensionDownload.Receive(fixture.Catalog.Instance, fixture.Catalog.Lease,
            fixture.Catalog.Resources, fixture.OperationId, entry, (stream, token) => stream.WriteAsync(bytes, token).AsTask(), default);
        var staging = download.ArchivePath; var original = PhysicalObjectIdentity.Read(staging);
        var path = download.Adopt(blobs, default);
        if (!existing) Assert.Equal(original, PhysicalObjectIdentity.Read(path));
        using (var root = fixture.Acquire())
        using (var package = VerifiedExtensionPackage.Prepare(fixture.Catalog.Store, ExtensionStoreChannel.Stable,
            fixture.OperationId, root, "fixture-plugin", path, fixture.Catalog.Contracts, default))
        {
            Assert.Equal(entry, package.Binding.Entry);
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, [1]));
        }
        download.Dispose();
        Assert.False(File.Exists(staging)); Assert.False(Directory.Exists(Path.GetDirectoryName(staging)));
        blobs.Verify(reference);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "Archive Adoption Rejects An Unknown Existing Target Without Overwriting Its Bytes")]
    public async Task PreservesUnknownTarget()
    {
        using var fixture = new BoundPackageFixture(); fixture.Publish();
        var bytes = File.ReadAllBytes(fixture.Archive);
        var entry = fixture.Catalog.Store.ReadCached(ExtensionStoreChannel.Stable)!.Entries.Single();
        using var download = await OwnedExtensionDownload.Receive(fixture.Catalog.Instance, fixture.Catalog.Lease,
            fixture.Catalog.Resources, fixture.OperationId, entry, (stream, token) => stream.WriteAsync(bytes, token).AsTask(), default);
        var target = Path.Combine(fixture.Catalog.Instance.RuntimeRoot, $"blobs/content/{entry.ArtifactSha256}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, [17, 21, 9]);
        Assert.Throws<IOException>(() => download.Adopt(fixture.Blobs, default));
        Assert.Equal(new byte[] { 17, 21, 9 }, File.ReadAllBytes(target));
    }

    [Theory(DisplayName = "An Actual Owned Download Pins The Original Archive Until Package Verification And Deletes Only Its Own Objects")]
    [InlineData(false)] [InlineData(true)]
    public async Task PinsOwnedArchive(bool chunked)
    {
        using var fixture = new BoundPackageFixture(); fixture.Publish();
        var bytes = File.ReadAllBytes(fixture.Archive);
        var entry = fixture.Catalog.Store.ReadCached(ExtensionStoreChannel.Stable)!.Entries.Single();
        await using var server = new StoreTransferFixture(chunked ? "" : $"Content-Length: {bytes.Length}\r\n", bytes, chunked);
        using var client = ProbeHttpClient.CreateStore(new(ProxyMode.None, null, ""), null);
        using var response = await client.GetAsync(server.Address, HttpCompletionOption.ResponseHeadersRead);
        var download = await OwnedExtensionDownload.Receive(fixture.Catalog.Instance, fixture.Catalog.Lease,
            fixture.Catalog.Resources, fixture.OperationId, entry,
            (stream, token) => StoreResponseReader.Artifact(response, stream, entry.ArtifactBytes, entry.ArtifactSha256, token), default);
        var path = download.ArchivePath;
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Throws<IOException>(() => File.WriteAllBytes(path, "replacement"u8.ToArray()));
        using (var root = fixture.Acquire())
        using (var package = VerifiedExtensionPackage.Prepare(fixture.Catalog.Store, ExtensionStoreChannel.Stable,
            fixture.OperationId, root, "fixture-plugin", path, fixture.Catalog.Contracts, default))
        {
            Assert.Equal(entry, package.Binding.Entry);
            Assert.Equal(PhysicalObjectIdentity.Read(path), package.Binding.ArchiveIdentity);
            Assert.NotEmpty(package.Files);
        }
        download.Dispose(); download.Dispose();
        Assert.False(File.Exists(path)); Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.True(Directory.Exists(Path.Combine(fixture.Catalog.Instance.RuntimeRoot, "staging")));
    }

    [Theory(DisplayName = "A Failed Transfer Cleans Only Its Actually Created Partial Archive And Exclusive Directory")]
    [InlineData("cancel")] [InlineData("transport")] [InlineData("digest")]
    public async Task RemovesOwnedPartials(string failure)
    {
        using var fixture = new BoundPackageFixture(); fixture.Publish();
        var original = File.ReadAllBytes(fixture.Archive);
        var entry = fixture.Catalog.Store.ReadCached(ExtensionStoreChannel.Stable)!.Entries.Single();
        using var cancellation = new CancellationTokenSource();
        var failed = OwnedExtensionDownload.Receive(fixture.Catalog.Instance, fixture.Catalog.Lease,
            fixture.Catalog.Resources, fixture.OperationId, entry, async (stream, token) =>
            {
                await stream.WriteAsync("actual partial owned bytes"u8.ToArray(), token);
                if (failure == "cancel") { await cancellation.CancelAsync(); token.ThrowIfCancellationRequested(); }
                if (failure == "transport") throw new IOException("controlled_transfer_truncated");
            }, cancellation.Token);
        if (failure == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failed);
        else await Assert.ThrowsAnyAsync<IOException>(() => failed);
        Assert.False(Directory.Exists(OperationDirectory(fixture)));
        Assert.Equal(original, File.ReadAllBytes(fixture.Archive));
    }

    [Fact(DisplayName = "An Existing Operation Directory Is Preserved And Never Adopted As This Downloads Owned Staging")]
    public async Task RejectsExistingStaging()
    {
        using var fixture = new BoundPackageFixture(); fixture.Publish();
        var entry = fixture.Catalog.Store.ReadCached(ExtensionStoreChannel.Stable)!.Entries.Single();
        var directory = OperationDirectory(fixture); Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "unknown.bin"); var bytes = Guid.NewGuid().ToByteArray(); File.WriteAllBytes(marker, bytes);
        var called = false;
        await Assert.ThrowsAnyAsync<IOException>(() => OwnedExtensionDownload.Receive(fixture.Catalog.Instance,
            fixture.Catalog.Lease, fixture.Catalog.Resources, fixture.OperationId, entry,
            (_, _) => { called = true; return Task.CompletedTask; }, default));
        Assert.False(called); Assert.Equal(bytes, File.ReadAllBytes(marker));
        Assert.False(File.Exists(Path.Combine(directory, "payload.zip")));
    }

    [Fact(DisplayName = "An Actual Independent Reference Prevents Creating Another Owned Download Namespace")]
    public async Task RequiresExclusiveReference()
    {
        using var fixture = new BoundPackageFixture(); fixture.Publish();
        var entry = fixture.Catalog.Store.ReadCached(ExtensionStoreChannel.Stable)!.Entries.Single();
        Directory.CreateDirectory(Path.Combine(fixture.Catalog.Instance.RuntimeRoot, "staging"));
        using var reference = fixture.Catalog.Resources.TryAcquire(Guid.NewGuid(),
            PhysicalResourceAdmission.Read(OperationDirectory(fixture), ResourceClaimMode.Reference)).Lease!;
        Assert.NotNull(reference); var called = false;
        await Assert.ThrowsAnyAsync<IOException>(() => OwnedExtensionDownload.Receive(fixture.Catalog.Instance,
            fixture.Catalog.Lease, fixture.Catalog.Resources, fixture.OperationId, entry,
            (_, _) => { called = true; return Task.CompletedTask; }, default));
        Assert.False(called); Assert.False(Directory.Exists(OperationDirectory(fixture)));
    }

    [Fact(DisplayName = "Download Cleanup Preserves An Unknown Additional Child Instead Of Recursively Removing The Directory")]
    public async Task PreservesUnknownChildren()
    {
        using var fixture = new BoundPackageFixture(); fixture.Publish();
        var bytes = File.ReadAllBytes(fixture.Archive);
        var entry = fixture.Catalog.Store.ReadCached(ExtensionStoreChannel.Stable)!.Entries.Single();
        var download = await OwnedExtensionDownload.Receive(fixture.Catalog.Instance, fixture.Catalog.Lease,
            fixture.Catalog.Resources, fixture.OperationId, entry,
            (stream, token) => stream.WriteAsync(bytes, token).AsTask(), default);
        var marker = Path.Combine(OperationDirectory(fixture), "unknown.bin");
        var unknown = Guid.NewGuid().ToByteArray(); File.WriteAllBytes(marker, unknown);
        Assert.Throws<IOException>(download.Dispose);
        Assert.Equal(unknown, File.ReadAllBytes(marker)); Assert.False(File.Exists(download.ArchivePath));
        Assert.True(Directory.Exists(OperationDirectory(fixture)));
    }

    private static string OperationDirectory(BoundPackageFixture fixture) => Path.Combine(
        fixture.Catalog.Instance.RuntimeRoot, "staging", "extension-" + fixture.OperationId.ToString("N"));
}
