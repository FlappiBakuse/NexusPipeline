using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Core.Configuration;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

public sealed class BlobScopeTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "Nested Readers Reuse One Protected Blob Without Rehashing Its Bytes For Every Historical Owner")]
    public void BoundsNestedVerificationReads()
    {
        var (instance, store) = Create();
        var bytes = Enumerable.Range(0, 65536).Select(index => (byte)(index % 251)).ToArray();
        var reference = store.PersistContent(bytes, default);
        var before = ReadBytes();
        using (BlobReadScope.Hold(instance))
            for (var owner = 0; owner < 128; owner++)
            {
                using var nested = BlobReadScope.Hold(instance);
                using var input = store.OpenVerified(reference);
                Assert.Equal(bytes[0], input.ReadByte());
            }
        var readBytes = ReadBytes() - before;
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { phase = "nested-blob-verification", owners = 128,
            contentBytes = bytes.Length, maximumReadBytes = 2097152, actualReadBytes = readBytes }));
        Assert.InRange(readBytes, 0UL, 2097152UL);
    }

    [Fact(DisplayName = "Nested Capture Reports Each Read Once While Its Same Runtime Outer Protection Survives")]
    public void SharesCapturedProtection()
    {
        var (instance, store) = Create(); var (foreign, other) = Create();
        var reference = store.PersistContent(new byte[] { 1, 2, 3 }, default);
        var foreignReference = other.PersistContent(new byte[] { 4, 5, 6 }, default);
        var path = Path.Combine(instance.RuntimeRoot, reference.RelativePath);
        var foreignPath = Path.Combine(foreign.RuntimeRoot, foreignReference.RelativePath);
        var seen = new List<string>();
        using (BlobReadScope.Hold(instance))
        {
            using (BlobReadScope.Capture(instance, seen.Add))
            {
                using (store.OpenVerified(reference)) { }
                using (store.OpenVerified(reference)) { }
            }
            Assert.Equal(new[] { Path.GetFullPath(path), Path.GetFullPath(path) }, seen.Select(Path.GetFullPath));
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, [9]));
            using (BlobReadScope.Hold(foreign)) using (other.OpenVerified(foreignReference)) { }
            File.WriteAllBytes(foreignPath, [9]);
            Assert.Throws<IOException>(() => other.OpenVerified(foreignReference));
        }
        var replaced = path + ".retained";
        File.Move(path, replaced); File.WriteAllBytes(path, [9]);
        using (BlobReadScope.Hold(instance)) Assert.Throws<IOException>(() => store.OpenVerified(reference));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(replaced));
    }

    [Fact(DisplayName = "A Failed Nested Read Publishes No Protection And A Later Read Verifies The Actual Bytes")]
    public void RechecksUnavailableReads()
    {
        var (instance, store) = Create();
        var reference = store.PersistContent(new byte[] { 1, 2, 3 }, default);
        var path = Path.Combine(instance.RuntimeRoot, reference.RelativePath);
        var seen = new List<string>();
        using (BlobReadScope.Capture(instance, seen.Add))
        {
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            using (BlobReadScope.Hold(instance)) Assert.Throws<IOException>(() => store.OpenVerified(reference));
            Assert.Empty(seen);
            using (BlobReadScope.Hold(instance)) using (store.OpenVerified(reference)) { }
            Assert.Single(seen);
            Assert.Throws<IOException>(() => File.Delete(path));
        }
        File.WriteAllBytes(path, [9]);
        using (BlobReadScope.Hold(instance)) Assert.Throws<IOException>(() => store.OpenVerified(reference));
    }

    [Fact(DisplayName = "Frozen Snapshot Reads Preserve Immutable Values And Recheck Changed Files After Protection Ends")]
    public void ProtectsSnapshotValues()
    {
        var (instance, store) = Create(); var codec = new ConfigurationSnapshotCodec(store);
        FileTreeEntry[] expected = [new("", FileObjectKind.Directory, 0, null), new("nested", FileObjectKind.Directory, 0, null)];
        var input = expected.ToArray();
        var manifest = codec.Write(new(FileObjectKind.Directory, input));
        input[1] = new("changed", FileObjectKind.Directory, 0, null);
        var missing = codec.Write(new(FileObjectKind.Missing, []));
        var path = Path.Combine(instance.RuntimeRoot, manifest.RelativePath); var bytes = File.ReadAllBytes(path);
        var (foreign, other) = Create(); var foreignCodec = new ConfigurationSnapshotCodec(other);
        var foreignManifest = foreignCodec.Write(new(FileObjectKind.Directory, [new("", FileObjectKind.Directory, 0, null)]));
        using (BlobReadScope.Hold(instance))
        {
            var tree = codec.Read(manifest);
            Assert.Equal(FileObjectKind.Directory, tree.Shape);
            Assert.Equal(expected, tree.Entries);
            if (tree.Entries is IList<FileTreeEntry> entries)
            {
                try { entries[1] = input[1]; }
                catch (NotSupportedException) { }
            }
            Assert.Equal(FileObjectKind.Missing, codec.Read(missing).Shape);
            Assert.Equal(expected, codec.Read(manifest).Entries);
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, [1, 2, 3]));
            Assert.Equal(FileObjectKind.Directory, foreignCodec.Read(foreignManifest).Shape);
            File.WriteAllBytes(Path.Combine(foreign.RuntimeRoot, foreignManifest.RelativePath), [1, 2, 3]);
            Assert.Throws<IOException>(() => foreignCodec.Read(foreignManifest));
            Assert.Equal(expected, codec.Read(manifest).Entries);
        }
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Throws<IOException>(() => codec.Read(manifest));
        File.WriteAllBytes(path, bytes);
        Assert.Equal(expected, codec.Read(manifest).Entries);
    }

    [Theory(DisplayName = "Verified Blob Reads Reject Missing Leaves And Live Or Broken Reparse Entries Without Following Them")]
    [InlineData("missing")]
    [InlineData("leaf-link")]
    [InlineData("parent-link")]
    [InlineData("broken-link")]
    public void RejectsReplacedEntries(string replacement)
    {
        var (instance, store) = Create();
        byte[] bytes = [1, 2, 3];
        var reference = store.PersistContent(bytes, default);
        var path = Path.Combine(instance.RuntimeRoot, reference.RelativePath);
        if (replacement == "missing")
        {
            File.Delete(path);
            Assert.Throws<IOException>(() => store.OpenVerified(reference));
            return;
        }
        var outside = instance.RuntimeRoot + "-outside";
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, Path.GetFileName(path));
        File.Copy(path, target);
        File.Delete(path);
        var link = replacement == "parent-link" ? Path.GetDirectoryName(path)! : path;
        if (replacement == "parent-link") Directory.Delete(link);
        ControlledJunction.Create(link, outside);
        try
        {
            if (replacement == "broken-link") { File.Delete(target); Directory.Delete(outside); }
            Assert.Throws<IOException>(() => store.OpenVerified(reference));
            if (replacement != "broken-link") Assert.Equal(bytes, File.ReadAllBytes(target));
        }
        finally { Directory.Delete(link); }
    }

    [Fact(DisplayName = "A Synchronous Blob Scope Protects Verified Bytes After Readers Close And Rechecks Changes After Release")]
    public void ProtectsVerifiedBytes()
    {
        var (instance, store) = Create();
        var bytes = new byte[] { 1, 2, 3, 4 };
        var reference = store.PersistContent(bytes, default);
        var path = Path.Combine(instance.RuntimeRoot, reference.RelativePath);
        using (BlobReadScope.Hold(instance))
        {
            using (store.OpenVerified(reference)) { }
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, [4, 3, 2, 1]));
            Assert.Throws<IOException>(() => File.Delete(path));
            using var second = store.OpenVerified(reference);
            var actual = new byte[bytes.Length];
            second.ReadExactly(actual);
            Assert.Equal(bytes, actual);
        }
        File.WriteAllBytes(path, [4, 3, 2, 1]);
        Assert.Equal("immutable_blob_mismatch", Assert.Throws<IOException>(() => store.OpenVerified(reference)).Message);
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "A Blob Scope Bounds Write Exclusion And Releases Its Protected Files")]
    public void BoundsRetainedFiles()
    {
        const int distinctObjects = 256;
        const int maximumProtectedFiles = 128;
        var (instance, store) = Create();
        var references = Enumerable.Range(0, distinctObjects)
            .Select(index => store.PersistContent(BitConverter.GetBytes(index), default)).ToArray();
        var retained = new List<string>();
        using (BlobReadScope.Hold(instance))
        {
            foreach (var item in references)
                using (BlobReadScope.Hold(instance)) using (store.OpenVerified(item)) { }
            BlobReference? released = null;
            foreach (var item in references)
            {
                var path = Path.Combine(instance.RuntimeRoot, item.RelativePath);
                try
                {
                    using var writer = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    released ??= item;
                }
                catch (IOException sharingViolation) when ((sharingViolation.HResult & 0xffff) == 32) { retained.Add(path); }
            }
            Assert.InRange(retained.Count, 1, maximumProtectedFiles);
            Assert.NotNull(released);
            File.WriteAllBytes(Path.Combine(instance.RuntimeRoot, released.RelativePath), [255, 255, 255, 255]);
            Assert.Equal("immutable_blob_mismatch", Assert.Throws<IOException>(() => store.OpenVerified(released)).Message);
        }
        foreach (var path in retained) using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { }
    }

    [Fact(DisplayName = "Nested Blob Scopes Promote Protection To Their Same Runtime Outer Owner And Release In Order")]
    public void PreservesNestedOwners()
    {
        var (instance, store) = Create();
        var first = store.PersistContent(new byte[] { 1 }, default);
        var second = store.PersistContent(new byte[] { 2 }, default);
        var firstPath = Path.Combine(instance.RuntimeRoot, first.RelativePath);
        var secondPath = Path.Combine(instance.RuntimeRoot, second.RelativePath);
        using var outer = BlobReadScope.Hold(instance);
        using (store.OpenVerified(first)) { }
        using (BlobReadScope.Hold(instance))
        {
            using (store.OpenVerified(second)) { }
            Assert.Throws<InvalidOperationException>(() => outer.Dispose());
            Assert.Throws<IOException>(() => File.WriteAllBytes(secondPath, [9]));
        }
        Assert.Throws<IOException>(() => File.WriteAllBytes(secondPath, [9]));
        Assert.Throws<IOException>(() => File.WriteAllBytes(firstPath, [9]));
        outer.Dispose();
        File.WriteAllBytes(firstPath, [9]);
        File.WriteAllBytes(secondPath, [9]);
    }

    [Fact(DisplayName = "Blob Protection Never Spills Into A Different Runtime")]
    public void ExcludesForeignRuntime()
    {
        var (instance, local) = Create();
        var (foreign, store) = Create();
        byte[] original = [1, 2];
        var reference = store.PersistContent(original, default);
        var localReference = local.PersistContent(original, default);
        var localPath = Path.Combine(instance.RuntimeRoot, localReference.RelativePath);
        var path = Path.Combine(foreign.RuntimeRoot, reference.RelativePath);
        using var scope = BlobReadScope.Hold(instance);
        using (local.OpenVerified(localReference)) { }
        using (store.OpenVerified(reference)) { }
        File.WriteAllBytes(path, [2, 1]);
        Assert.Equal("immutable_blob_mismatch", Assert.Throws<IOException>(() => store.OpenVerified(reference)).Message);
        Assert.Throws<IOException>(() => File.WriteAllBytes(localPath, [2, 1]));
        Assert.Equal(original, File.ReadAllBytes(localPath));
    }

    private static (RuntimeInstance Instance, ImmutableBlobStore Store) Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException(), Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(root);
        return (instance, new(instance, contracts));
    }

    private static ulong ReadBytes()
    {
        if (!GetProcessIoCounters(new IntPtr(-1), out var counters)) throw new IOException("native_process_io_unavailable");
        return counters.ReadBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
