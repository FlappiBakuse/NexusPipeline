using System.Runtime.Versioning;
using System.Net;
using System.Security.Cryptography;
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
using NexusPipeline.Platform.Storage.Maintenance;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
[Collection("SerialNative")]
public sealed class OwnedDownloadTests
{
    [Theory(DisplayName = "Real Update And Package Services Keep Shared Download Ownership Across Success Cancellation Invalid Bytes Replacement And Cleanup Failure")]
    [InlineData(false, "success")] [InlineData(true, "success")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "size")] [InlineData(true, "size")]
    [InlineData(false, "digest")] [InlineData(true, "digest")]
    [InlineData(false, "replace")] [InlineData(true, "replace")]
    [InlineData(false, "cleanup")] [InlineData(true, "cleanup")]
    public async Task ProtectsServiceDownloads(bool update, string fault)
    {
        if (update) await UpdateDownload(fault);
        else await PackageDownload(fault);
    }

    private static async Task PackageDownload(string fault)
    {
        using var stop = new CancellationTokenSource(); DownloadBodyFixture? body = null;
        await using var fixture = new PackageServiceFixture(artifactContent: () => body!.Content());
        var owner = fixture.Package.Catalog;
        body = new(File.ReadAllBytes(fixture.Package.Archive), owner.Instance.RuntimeRoot, fault, stop);
        var catalog = await fixture.Service.RefreshAsync("local", ExtensionStoreChannel.Stable, default);
        var error = await Record.ExceptionAsync(async () =>
        {
            var details = await fixture.Service.CatalogDetailsAsync(catalog.Identity, "fixture-plugin", stop.Token);
            Assert.NotNull(details); Assert.Equal("fixture-plugin", details.Manifest.GetProperty("extensionId").GetString());
        });
        RequireTransferResult(body, fault, error, update: false);
        body.RequireReleased(owner.Resources); Assert.Empty(fixture.Runtime.ReadStates());
    }

    private static async Task UpdateDownload(string fault)
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.UpdateApply, PayloadAction.Upgrade);
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var index = new RecoveryIndex(); index.PublishVerified(0, 1, []);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        var source = new UpdateSourceCodec(fixture.Instance, new()).ReadMetadata(fixture.Plan.MaintenanceSource!);
        var bytes = File.ReadAllBytes(Path.Combine(fixture.Instance.RuntimeRoot, source.Archive.RelativePath));
        using var stop = new CancellationTokenSource();
        var body = new DownloadBodyFixture(bytes, fixture.Instance.RuntimeRoot, fault, stop);
        using var signer = ECDsa.Create(); signer.ImportPkcs8PrivateKey(UpdateSourceSeed.TestSigningKey(), out _);
        var trust = new UpdateReleaseTrust(new Dictionary<string, byte[]> { ["fixture-only"] = signer.ExportSubjectPublicKeyInfo() },
            new("https://updates.example.test/metadata/update/1/"), new("https://store.example.test/metadata/store/1/"),
            [new("https://assets.example.test/releases/download/")], 1, 1);
        await using var service = new HostUpdateService(fixture.Instance, fixture.Database, lease, resources, new(), fixture.Roots,
            trust, () => new HttpClient(new DownloadHandler(body)));
        var request = new HostUpdateRequest(Guid.NewGuid(), "download-fixture", source.Feed.Identity,
            source.HostVersion, source.Timing, source.Window);
        var error = await Record.ExceptionAsync(() => service.PrepareAsync(request, stop.Token));
        RequireTransferResult(body, fault, error, update: true);
        body.RequireReleased(resources);
        Assert.Empty(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
    }

    private static void RequireTransferResult(DownloadBodyFixture body, string fault, Exception? error, bool update)
    {
        if (fault == "cleanup") Assert.StartsWith(update ? "maintenance_download_cleanup_pending" : "extension_download_cleanup_pending",
            Assert.IsType<AggregateException>(error).Message);
        else if (fault == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else if (fault is "size" or "digest") Assert.Equal("extension_store_download_invalid", Assert.IsType<IOException>(error).Message);
        else
        {
            Assert.True(body.ReadComplete);
            if (update) Assert.Equal("update_payload_profile_invalid", Assert.IsType<IOException>(error).Message);
            else Assert.Null(error);
            if (fault == "replace") Assert.True(body.ReplacementBlocked);
        }
    }

    private sealed class DownloadHandler(DownloadBodyFixture body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = body.Content() });
        }
    }

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
