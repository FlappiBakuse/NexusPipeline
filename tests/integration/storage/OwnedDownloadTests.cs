using System.Runtime.Versioning;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
[Collection("SerialNative")]
public sealed class OwnedDownloadTests
{
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
