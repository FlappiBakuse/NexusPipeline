using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Maintenance;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class BootstrapArchiveTests
{
    [Theory]
    [InlineData("")]
    [InlineData("maintenance/")]
    [InlineData("tools/maintenance/")]
    public void MaterializesHelperClosure(string prefix)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var source = Path.Combine(root, "candidate");
        Directory.CreateDirectory(Path.Combine(source, prefix, "locales"));
        Directory.CreateDirectory(Path.Combine(source, "desktop"));
        File.WriteAllText(Path.Combine(source, prefix, "helper.exe"), "helper");
        File.WriteAllText(Path.Combine(source, prefix, "dependency.dll"), "dependency");
        File.WriteAllText(Path.Combine(source, prefix, "locales/en.json"), "{}");
        File.WriteAllText(Path.Combine(source, "desktop/renderer.exe"), "desktop");
        File.WriteAllText(Path.Combine(source, "host.exe"), "host");
        File.WriteAllText(Path.Combine(source, "maintenance-extra.dll"), "outside");
        var archiver = new FileTreeArchive(new(SnapshotBudget.Default), new(instance, new()));
        var candidate = archiver.Capture(source, default);
        var selected = BootstrapArchive.Select(candidate, prefix + "helper.exe");
        var output = Path.Combine(root, "bootstrap");
        archiver.MaterializeNew(selected, output, default);
        Assert.Equal("helper", File.ReadAllText(Path.Combine(output, prefix, "helper.exe")));
        Assert.Equal([prefix + "helper.exe"], Directory.GetFiles(output, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(output, path).Replace('\\', '/')));
        Assert.False(Directory.Exists(Path.Combine(output, prefix, "locales")));
        Assert.False(Directory.Exists(Path.Combine(output, "desktop")));
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var actual = selected with { OriginalObjects = PhysicalTreeIdentity.Capture(output, scanner.Capture(output, default)) };
        using (TreeReadLease.Acquire(output, PhysicalObjectIdentity.Read(root), actual))
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(output, prefix, "helper.exe"), "changed"));
        File.WriteAllText(Path.Combine(output, "foreign.dll"), "foreign");
        Assert.Throws<IOException>(() => TreeReadLease.Acquire(output, PhysicalObjectIdentity.Read(root), actual));
        Assert.Throws<IOException>(() => BootstrapArchive.Select(candidate, "missing.exe"));
        Assert.Throws<IOException>(() => BootstrapArchive.Select(candidate, "../helper.exe"));
    }
    [Fact(DisplayName = "Borrowed Verified Tree Inputs Stay Read Only After Their Preparing Owner Releases")]
    public void SharesPinnedInputs()
    {
        var (root, source, archive) = CreateTree();
        var lease = TreeReadLease.Acquire(source, PhysicalObjectIdentity.Read(root), archive);
        var borrowed = lease.Borrow();
        lease.Dispose(); lease.Dispose();
        try
        {
            Assert.Throws<ObjectDisposedException>(() => lease.Borrow());
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(source, "input.bin"), "changed"));
            Assert.Throws<IOException>(() => Directory.Move(source, source + "-moved"));
            Assert.Equal("original", File.ReadAllText(Path.Combine(source, "input.bin")));
        }
        finally { borrowed.Dispose(); borrowed.Dispose(); }
        File.WriteAllText(Path.Combine(source, "input.bin"), "released");
        Directory.Move(source, source + "-moved");
    }

    [Theory(DisplayName = "Borrowing Protected Tree Bytes Still Rejects Added Files Directories And Live Or Broken Junctions")]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("junction")]
    [InlineData("broken-junction")]
    public void RejectsExpandedTrees(string kind)
    {
        var (root, source, archive) = CreateTree();
        using var lease = TreeReadLease.Acquire(source, PhysicalObjectIdentity.Read(root), archive);
        var extra = Path.Combine(source, "extra");
        if (kind == "file") File.WriteAllText(extra, "extra");
        else if (kind == "directory") Directory.CreateDirectory(extra);
        else
        {
            var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
            ControlledJunction.Create(extra, outside);
            if (kind == "broken-junction") Directory.Delete(outside);
        }
        try { Assert.Throws<IOException>(() => lease.Borrow()); }
        finally { if (kind == "file") File.Delete(extra); else Directory.Delete(extra); }
        using var restored = lease.Borrow();
        Assert.Equal("original", File.ReadAllText(Path.Combine(source, "input.bin")));
    }

    private static (string Root, string Source, ArchivedFileTree Archive) CreateTree()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "tree-borrow-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "input.bin"), "original");
        return (root, source, new FileTreeArchive(new(SnapshotBudget.Default), new(instance, new())).Capture(source, default));
    }

}
