using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class BlobScopeTests
{
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
        var (instance, store) = Create();
        var references = Enumerable.Range(0, BlobReadScope.MaximumReferences + 1)
            .Select(index => store.PersistContent(BitConverter.GetBytes(index), default)).ToArray();
        var retained = new List<string>();
        using (BlobReadScope.Hold(instance))
        {
            foreach (var item in references) using (store.OpenVerified(item)) { }
            BlobReference? released = null;
            foreach (var item in references)
            {
                var path = Path.Combine(instance.RuntimeRoot, item.RelativePath);
                try
                {
                    using var writer = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    released ??= item;
                }
                catch (IOException) { retained.Add(path); }
            }
            Assert.InRange(retained.Count, 1, BlobReadScope.MaximumReferences);
            Assert.NotNull(released);
            File.WriteAllBytes(Path.Combine(instance.RuntimeRoot, released.RelativePath), [255, 255, 255, 255]);
            Assert.Equal("immutable_blob_mismatch", Assert.Throws<IOException>(() => store.OpenVerified(released)).Message);
        }
        foreach (var path in retained) using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { }
    }

    [Fact(DisplayName = "Nested Blob Scopes Release Only Their Own Protection In Owner Order")]
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
        File.WriteAllBytes(secondPath, [9]);
        Assert.Throws<IOException>(() => File.WriteAllBytes(firstPath, [9]));
        outer.Dispose();
        File.WriteAllBytes(firstPath, [9]);
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
}
