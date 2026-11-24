using System.Text.Json;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

public sealed class TreeArchiveTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "Protected Archives Read Each Source Once And Copy Only Content Missing From The Blob Store")]
    public void BoundsProtectedReads()
    {
        var (root, archive) = Create();
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        var firstFile = Path.Combine(source, "first.bin");
        var firstBytes = Enumerable.Range(0, 32768).Select(index => (byte)index).ToArray();
        var secondBytes = Enumerable.Range(0, 65536).Select(index => (byte)(index + 3)).ToArray();
        File.WriteAllBytes(firstFile, firstBytes);
        File.WriteAllBytes(Path.Combine(source, "second.bin"), secondBytes);
        var parent = PhysicalObjectIdentity.Read(root);
        var stderr = Console.Error;
        using var captured = new StringWriter();
        try
        {
            Console.SetError(captured);
            using var profile = StartupProfile.Begin("protected-configuration-archive");
            using (var scene = archive.CaptureProtected(source, parent, default, out var cold))
            {
                Assert.Equal(new[] { "", "empty", "first.bin", "second.bin" }, cold.Snapshot.Entries.Select(entry => entry.RelativePath));
                Assert.Equal(new long[] { firstBytes.Length, secondBytes.Length }, cold.Contents.Select(content => content.SizeBytes));
                var copied = Path.Combine(root, "reconstructed");
                _ = archive.MaterializeNew(cold, copied, default);
                Assert.Equal(firstBytes, File.ReadAllBytes(Path.Combine(copied, "first.bin")));
                Assert.Equal(secondBytes, File.ReadAllBytes(Path.Combine(copied, "second.bin")));
                Assert.Throws<IOException>(() => File.Open(firstFile, FileMode.Open, FileAccess.Write, FileShare.Read));
                var unlisted = Path.Combine(source, "empty", "unlisted");
                File.WriteAllText(unlisted, "external");
                Assert.Throws<IOException>(() => scene.RequireNamespace());
                File.Delete(unlisted);
            }
            using var hit = archive.CaptureProtected(source, parent, default, out var archived);
            Assert.Equal(2, archived.Contents.Count);
        }
        finally { Console.SetError(stderr); }
        using var report = JsonDocument.Parse(captured.ToString());
        var steps = report.RootElement.GetProperty("steps").EnumerateArray().ToDictionary(
            step => step.GetProperty("name").GetString()!, step => step.GetProperty("count").GetInt64());
        Assert.Equal(4, steps["configuration.capture.hash"]);
        Assert.Equal(0, steps.GetValueOrDefault("configuration.capture.copy"));
        Assert.Equal(2, steps.GetValueOrDefault("configuration.capture.buffer-write"));
    }

    [Fact(DisplayName = "A Large Protected File Streams Once And Only A New Immutable Blob Is Flushed")]
    public void BoundsLargeProtectedReads()
    {
        var (root, archive) = Create();
        var source = Path.Combine(root, "source.bin");
        var bytes = new byte[4 * 1024 * 1024 + 3];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index * 17);
        File.WriteAllBytes(source, bytes);
        var parent = PhysicalObjectIdentity.Read(root);
        var cold = Capture("cold", out var created);
        var warm = Capture("warm", out var reused);
        var reference = Assert.Single(created.Contents);
        Assert.Equal(reference, Assert.Single(reused.Contents));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), reference.Sha256);
        Assert.Equal(bytes.LongLength, reference.SizeBytes);
        var restored = Path.Combine(root, "restored.bin");
        _ = archive.MaterializeNew(created, restored, default);
        Assert.Equal(bytes, File.ReadAllBytes(restored));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "runtime", "blobs", "content"), "*.staging"));
        output.WriteLine($"{bytes.LongLength} source bytes: cold {Summary(cold)}; warm {Summary(warm)}");
        Assert.Equal(0, cold.GetValueOrDefault("configuration.capture.copy"));
        Assert.Equal(1, cold.GetValueOrDefault("configuration.capture.stream"));
        Assert.Equal(1, cold.GetValueOrDefault("configuration.capture.stream.flush"));
        Assert.Equal(0, warm.GetValueOrDefault("configuration.capture.copy"));
        Assert.Equal(1, warm.GetValueOrDefault("configuration.capture.stream"));
        Assert.Equal(0, warm.GetValueOrDefault("configuration.capture.stream.flush"));

        static string Summary(Dictionary<string, long> steps) => string.Join(", ", steps
            .Where(item => item.Key.StartsWith("configuration.capture.", StringComparison.Ordinal))
            .Select(item => $"{item.Key}={item.Value}"));

        Dictionary<string, long> Capture(string phase, out ArchivedFileTree snapshot)
        {
            using var captured = new StringWriter();
            var stderr = Console.Error;
            try
            {
                Console.SetError(captured);
                using var profile = StartupProfile.Begin(phase);
                using var scene = archive.CaptureProtected(source, parent, default, out snapshot);
            }
            finally { Console.SetError(stderr); }
            using var report = JsonDocument.Parse(captured.ToString());
            return report.RootElement.GetProperty("steps").EnumerateArray().ToDictionary(
                step => step.GetProperty("name").GetString()!, step => step.GetProperty("count").GetInt64());
        }
    }

    [Fact(DisplayName = "Two Streamed Captures Can Publish The Same Digest While A Reader Opens The Visible Blob")]
    public async Task ReadsStreamedPublicationConcurrently()
    {
        var (root, archive) = Create();
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenExisting(Path.Combine(root, "runtime"));
        var blobs = new ImmutableBlobStore(instance, contracts);
        var parent = PhysicalObjectIdentity.Read(root);
        var bytes = Enumerable.Range(0, 1024 * 1024 + 1).Select(index => (byte)index).ToArray();
        for (var round = 0; round < 8; round++)
        {
            bytes[0] = (byte)(round + 1);
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var reference = new BlobReference($"blobs/content/{digest}.bin", bytes.Length, digest);
            var destination = Path.Combine(instance.RuntimeRoot, reference.RelativePath);
            var sources = new[] { Path.Combine(root, "first"), Path.Combine(root, "second") };
            foreach (var source in sources) File.WriteAllBytes(source, bytes);
            using var started = new Barrier(3);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var writers = sources.Select(source => Task.Factory.StartNew(() =>
            {
                started.SignalAndWait(deadline.Token);
                using var captured = archive.CaptureProtected(source, parent, deadline.Token, out var frozen);
                Assert.Equal(reference, Assert.Single(frozen.Contents));
            }, deadline.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            var reader = Task.Factory.StartNew(() =>
            {
                started.SignalAndWait(deadline.Token);
                var wait = new SpinWait();
                while (!File.Exists(destination))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (wait.NextSpinWillYield) Thread.Yield(); else wait.SpinOnce();
                }
                using var opened = blobs.OpenVerified(reference);
                Assert.Equal(reference.SizeBytes, opened.Length);
            }, deadline.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await Task.WhenAll(writers.Append(reader));
        }
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(instance.RuntimeRoot, "blobs", "content"), "*.staging"));
    }

    [Theory(DisplayName = "Archived Configuration Reconstructs Exact Shape And Bytes At An Exclusive New Path")]
    [InlineData("missing")]
    [InlineData("file")]
    [InlineData("directory")]
    public void ReconstructsExactTrees(string shape)
    {
        var (root, archive) = Create();
        var source = Path.Combine(root, "source");
        byte[] archived = [0, 255, 7];
        byte[] changed = [3, 4, 5];
        var input = shape == "file" ? source : Path.Combine(source, "嵌套", "配置.bin");
        if (shape == "directory")
        {
            Directory.CreateDirectory(Path.Combine(source, "嵌套", "空目录"));
            File.WriteAllBytes(Path.Combine(source, "empty.bin"), []);
            File.WriteAllBytes(Path.Combine(source, "duplicate.bin"), archived);
        }
        if (shape != "missing") File.WriteAllBytes(input, archived);
        var original = shape == "missing" ? null : PhysicalObjectIdentity.Read(source);
        var frozen = archive.Capture(source, CancellationToken.None);
        if (original is not null) File.WriteAllBytes(input, changed);

        var destination = Path.Combine(root, "candidate");
        var identity = archive.MaterializeNew(frozen, destination, CancellationToken.None);
        if (shape == "missing")
        {
            Assert.Null(identity);
            Assert.False(Path.Exists(destination));
            Assert.False(Path.Exists(source));
            return;
        }
        Assert.Equal(PhysicalObjectIdentity.Read(destination), identity);
        Assert.NotEqual(original, identity);
        Assert.Equal(original, PhysicalObjectIdentity.Read(source));
        Assert.Equal(changed, File.ReadAllBytes(input));
        if (shape == "file")
        {
            Assert.Equal(archived, File.ReadAllBytes(destination));
            return;
        }
        var paths = Directory.EnumerateFileSystemEntries(destination, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(destination, path).Replace('\\', '/')).Order(StringComparer.Ordinal);
        Assert.Equal(new[] { "duplicate.bin", "empty.bin", "嵌套", "嵌套/空目录", "嵌套/配置.bin" }.Order(StringComparer.Ordinal), paths);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(destination, "嵌套", "空目录")));
        Assert.Equal(archived, File.ReadAllBytes(Path.Combine(destination, "嵌套", "配置.bin")));
        Assert.Equal(archived, File.ReadAllBytes(Path.Combine(destination, "duplicate.bin")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(destination, "empty.bin")));
    }

    [Theory(DisplayName = "Existing Destination Bytes And Shape Are Never Replaced")]
    [InlineData("file")]
    [InlineData("directory")]
    public void PreservesExistingDestinations(string shape)
    {
        var (root, archive) = Create();
        var source = Path.Combine(root, "source");
        File.WriteAllText(source, "candidate");
        var destination = Path.Combine(root, "existing");
        var existing = shape == "file" ? destination : Path.Combine(destination, "unknown");
        if (shape == "directory") Directory.CreateDirectory(destination);
        File.WriteAllText(existing, "preserve");
        var original = PhysicalObjectIdentity.Read(destination);
        var content = PhysicalObjectIdentity.Read(existing);
        var frozen = archive.Capture(source, CancellationToken.None);
        Assert.Equal("file_tree_destination_not_empty", Assert.Throws<IOException>(() =>
            archive.MaterializeNew(frozen, destination, CancellationToken.None)).Message);
        Assert.Equal(original, PhysicalObjectIdentity.Read(destination));
        Assert.Equal(content, PhysicalObjectIdentity.Read(existing));
        Assert.Equal("preserve", File.ReadAllText(existing));
        if (shape == "directory") Assert.Equal(existing, Assert.Single(Directory.EnumerateFileSystemEntries(destination)));
    }

    [Fact(DisplayName = "Corrupt Archive Content Is Detected Before Creating Any Destination Object")]
    public void RejectsCorruptContent()
    {
        var (root, archive) = Create();
        var source = Path.Combine(root, "source");
        File.WriteAllText(source, "candidate");
        var frozen = archive.Capture(source, CancellationToken.None);
        File.WriteAllText(Path.Combine(root, "runtime", Assert.Single(frozen.Contents).RelativePath), "tampered!");
        var destination = Path.Combine(root, "candidate");
        Assert.Equal("immutable_blob_mismatch",
            Assert.Throws<IOException>(() => archive.MaterializeNew(frozen, destination, CancellationToken.None)).Message);
        Assert.False(Path.Exists(destination));
        Assert.Equal("candidate", File.ReadAllText(source));
    }

    [Theory(DisplayName = "Invalid Archive Paths Cannot Create Partial Objects Or Escape The Destination")]
    [InlineData("../outside", "unsupported_filesystem_component")]
    [InlineData("absent/child", "file_tree_archive_parent_missing")]
    public void RejectsInvalidPaths(string path, string reason)
    {
        var (root, archive) = Create();
        var entries = new[] { new FileTreeEntry("", FileObjectKind.Directory, 0, null),
            new FileTreeEntry(path, FileObjectKind.Directory, 0, null) };
        var frozen = new ArchivedFileTree(new(FileObjectKind.Directory, entries), [], []);
        var destination = Path.Combine(root, "candidate");
        Assert.Equal(reason, Assert.Throws<IOException>(() => archive.MaterializeNew(frozen, destination, CancellationToken.None)).Message);
        Assert.False(Path.Exists(destination));
        Assert.False(Path.Exists(Path.Combine(root, "outside")));
    }

    private static (string Root, FileTreeArchive Archive) Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException(), Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        return (root, new(scanner, new ImmutableBlobStore(instance, contracts)));
    }
}
