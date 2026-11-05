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
        var reference = store.PersistContent(new byte[] { 1, 2, 3 }, default);
        var path = Path.Combine(instance.RuntimeRoot, reference.RelativePath);
        var outside = Path.Combine(instance.RuntimeRoot, "outside");
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, Path.GetFileName(path));
        File.Copy(path, target);
        File.Delete(path);
        if (replacement == "missing")
        {
            Assert.Throws<IOException>(() => store.OpenVerified(reference));
            return;
        }
        var link = replacement == "parent-link" ? Path.GetDirectoryName(path)! : path;
        if (replacement == "parent-link") Directory.Delete(link);
        ControlledJunction.Create(link, outside);
        try
        {
            if (replacement == "broken-link") { File.Delete(target); Directory.Delete(outside); }
            Assert.Throws<IOException>(() => store.OpenVerified(reference));
            if (replacement != "broken-link") Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target));
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
            using (var first = store.OpenVerified(reference)) Assert.Equal(bytes[0], first.ReadByte());
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, [4, 3, 2, 1]));
            Assert.Throws<IOException>(() => File.Delete(path));
            using var second = store.OpenVerified(reference);
            Assert.Equal(0, second.Position); Assert.Equal(bytes[0], second.ReadByte());
            Assert.Throws<IOException>(() => store.OpenVerified(reference with { SizeBytes = 5 }));
        }
        File.WriteAllBytes(path, [4, 3, 2, 1]);
        Assert.Throws<IOException>(() => store.OpenVerified(reference));
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "The Blob Scope Evicts The Least Recently Opened Protection At Its Fixed Handle Bound")]
    public void BoundsRetainedFiles()
    {
        var (instance, store) = Create();
        var references = Enumerable.Range(0, BlobReadScope.MaximumReferences + 1)
            .Select(index => store.PersistContent(BitConverter.GetBytes(index), default)).ToArray();
        string PathFor(int index) => Path.Combine(instance.RuntimeRoot, references[index].RelativePath);
        using (BlobReadScope.Hold(instance))
        {
            foreach (var item in references.Take(BlobReadScope.MaximumReferences)) using (store.OpenVerified(item)) { }
            using (store.OpenVerified(references[0])) { }
            using (store.OpenVerified(references[^1])) { }
            File.WriteAllBytes(PathFor(1), [9]);
            Assert.Throws<IOException>(() => File.WriteAllBytes(PathFor(0), [9]));
            Assert.Throws<IOException>(() => store.OpenVerified(references[1]));
        }
        File.WriteAllBytes(PathFor(0), [9]);
    }

    [Fact(DisplayName = "Nested Blob Scopes Release Only Their Own Protection In Owner Order")]
    public void PreservesNestedOwners()
    {
        var (instance, store) = Create();
        var first = store.PersistContent(new byte[] { 1 }, default);
        var second = store.PersistContent(new byte[] { 2 }, default);
        var outer = BlobReadScope.Hold(instance);
        try
        {
            using (store.OpenVerified(first)) { }
            using (var inner = BlobReadScope.Hold(instance))
            {
                using (store.OpenVerified(second)) { }
                Assert.Throws<InvalidOperationException>(() => outer.Dispose());
            }
            File.WriteAllBytes(Path.Combine(instance.RuntimeRoot, second.RelativePath), [9]);
            Assert.Throws<IOException>(() => File.WriteAllBytes(Path.Combine(instance.RuntimeRoot, first.RelativePath), [9]));
        }
        finally { outer.Dispose(); }
        File.WriteAllBytes(Path.Combine(instance.RuntimeRoot, first.RelativePath), [9]);
    }

    [Fact(DisplayName = "Blob Protection Never Spills Into A Different Runtime")]
    public void ExcludesForeignRuntime()
    {
        var (instance, _) = Create();
        var (foreign, store) = Create();
        var reference = store.PersistContent(new byte[] { 1, 2 }, default);
        var path = Path.Combine(foreign.RuntimeRoot, reference.RelativePath);
        using var scope = BlobReadScope.Hold(instance);
        using (store.OpenVerified(reference)) { }
        File.WriteAllBytes(path, [2, 1]);
        Assert.Throws<IOException>(() => store.OpenVerified(reference));
    }

    private static (RuntimeInstance Instance, ImmutableBlobStore Store) Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException(), Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(root);
        return (instance, new(instance, contracts));
    }
}
