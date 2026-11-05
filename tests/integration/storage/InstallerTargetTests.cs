using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerTargetTests
{
    [Fact(DisplayName = "Native Setup Freezes A Complete Independent Target Manifest In Its Protected Checkpoint")]
    public void FreezesTarget()
    {
        using var fixture = new InstallerMetadataFixture();
        var bytes = Manifest(fixture.Root);
        var expected = bytes.ToArray();
        var target = InstallerTargetManifest.Read(bytes, InstallerIdentityFixture.Digest(bytes), "test");
        bytes[0] = 0;
        var store = new InstallerCheckpointStore(fixture.Paths, "test"); var id = Guid.NewGuid();
        var frame = store.Create(fixture.Application, id, target.Sha256, InstallerRetainedFixture.Parent(), target: target);
        Assert.Equal(expected, frame.TargetManifest);
        Assert.Equal(expected, store.Read(fixture.Application, id).TargetManifest);
        Assert.Equal(expected, store.ReadPending(fixture.Application).TargetManifest);
        frame.TargetManifest![0] = 0;
        Assert.Equal(expected, store.Read(fixture.Application, id).TargetManifest);
    }

    [Theory(DisplayName = "Setup Rejects A Corrupt Wrong Profile Or Unknown Target Manifest Before Creating Ownership State")]
    [InlineData("digest")]
    [InlineData("json")]
    [InlineData("extra")]
    [InlineData("generation")]
    [InlineData("path")]
    [InlineData("profile")]
    [InlineData("production")]
    public void RejectsInvalidTarget(string fault)
    {
        using var fixture = new InstallerMetadataFixture(seed: false);
        var bytes = Manifest(fixture.Root);
        if (fault == "json") bytes = "{invalid"u8.ToArray();
        if (fault is "extra" or "generation" or "path" or "production")
        {
            var json = JsonNode.Parse(bytes)!.AsObject();
            if (fault == "extra") json["unknown"] = true;
            if (fault == "generation") json["installationGeneration"] = "g0176";
            if (fault == "path") json["files"]![0]!["path"] = "../outside.exe";
            if (fault == "production") { json["sourceSha"] = null; json["sourceTreeSha"] = null; }
            bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        }
        var digest = fault == "digest" ? new string('a', 64) : InstallerIdentityFixture.Digest(bytes);
        var profile = fault == "profile" ? "unknown" : fault == "production" ? "production" : "test";
        Assert.ThrowsAny<Exception>(() => InstallerTargetManifest.Read(bytes, digest, profile));
        Assert.False(Directory.Exists(fixture.Paths.ManagerRoot));
    }

    [Theory(DisplayName = "A Checkpoint Cannot Bind A Target From A Different Digest Profile Or Ownership Scope")]
    [InlineData("digest")]
    [InlineData("profile")]
    [InlineData("scope")]
    public void RequiresBoundTarget(string fault)
    {
        using var fixture = new InstallerMetadataFixture(seed: false); Directory.CreateDirectory(fixture.Application);
        var bytes = Manifest(fixture.Root);
        var target = InstallerTargetManifest.Read(bytes, InstallerIdentityFixture.Digest(bytes), "test");
        var store = new InstallerCheckpointStore(fixture.Paths, fault == "profile" ? "production" : "test",
            fault == "scope" ? "ownership" : "installation");
        Assert.Throws<IOException>(() => store.Create(fixture.Application, Guid.NewGuid(), fault == "digest" ? new('a', 64) : target.Sha256,
            InstallerRetainedFixture.Parent(), target: target));
        Assert.False(Directory.Exists(fixture.Paths.ManagerRoot));
    }

    [Fact(DisplayName = "A Successor Checkpoint Cannot Drop The Frozen Native Setup Target")]
    public void PreservesTargetChain()
    {
        using var fixture = new InstallerMetadataFixture();
        var bytes = Manifest(fixture.Root); var target = InstallerTargetManifest.Read(bytes, InstallerIdentityFixture.Digest(bytes), "test");
        var store = new InstallerCheckpointStore(fixture.Paths, "test"); var id = Guid.NewGuid();
        store.Create(fixture.Application, id, target.Sha256, InstallerRetainedFixture.Parent(), target: target);
        var originalPath = Path.Combine(fixture.Paths.ManagerRoot, "metadata-checkpoint/prepared.protected");
        var original = File.ReadAllBytes(originalPath);
        var planned = store.Plan(fixture.Application, id, store.Capture(fixture.Application));
        var changed = InstallerCheckpointCodec.Seal(planned with { TargetManifest = null });
        var path = Path.Combine(fixture.Paths.ManagerRoot, "metadata-checkpoint/planned.protected");
        File.WriteAllBytes(path, changed);
        Assert.Throws<IOException>(() => store.Read(fixture.Application, id));
        Assert.Equal(original, File.ReadAllBytes(originalPath)); Assert.Equal(changed, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "The Complete Supported Manifest Budget Roundtrips Through Native Setup Checkpoints")]
    public void PreservesLargeTarget()
    {
        using var fixture = new InstallerMetadataFixture();
        var bytes = Manifest(fixture.Root).Concat(Enumerable.Repeat((byte)' ', 1200000)).ToArray();
        var target = InstallerTargetManifest.Read(bytes, InstallerIdentityFixture.Digest(bytes), "test");
        var store = new InstallerCheckpointStore(fixture.Paths, "test"); var id = Guid.NewGuid();
        store.Create(fixture.Application, id, target.Sha256, InstallerRetainedFixture.Parent(), target: target);
        Assert.Equal(bytes, store.ReadPending(fixture.Application).TargetManifest);
        store.Plan(fixture.Application, id, store.Capture(fixture.Application));
        Assert.Equal(bytes, store.Read(fixture.Application, id).TargetManifest);
        store.Observe(fixture.Application, id);
        Assert.Equal(bytes, store.RequireCurrentSetup(fixture.Application, id).TargetManifest);
    }

    [Fact(DisplayName = "Ordinary Protected Secret Callers Retain Their Existing Output Size Boundary")]
    public void PreservesSecretBudget()
    {
        var error = Assert.Throws<IOException>(() => WindowsDataProtection.Transform(new byte[1200000], "Controlled budget"u8.ToArray(), protect: true));
        Assert.Equal("secret_protection_output_invalid", error.Message);
    }

    [Theory(DisplayName = "The Frozen Setup Target Requires Every Original Expected Installed Application File")]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiresInstalledTarget(bool changed)
    {
        using var fixture = new InstallerMetadataFixture(seed: false);
        var bytes = Manifest(fixture.Root); var target = InstallerTargetManifest.Read(bytes, InstallerIdentityFixture.Digest(bytes), "test");
        var application = Path.Combine(fixture.Root, "target");
        if (changed) File.AppendAllText(Path.Combine(application, "README.md"), "Unrecognized target bytes");
        var snapshot = ApplicationTree.Capture(new(SnapshotBudget.Default), application, default);
        if (changed) Assert.Throws<IOException>(() => target.RequireInstalled(snapshot));
        else target.RequireInstalled(snapshot);
    }

    [Theory(DisplayName = "The Actual Installer Worker Refuses Bad Target Inputs Before Preparing An Installation")]
    [InlineData("digest")]
    [InlineData("schema")]
    [InlineData("missing")]
    public async Task RejectsThroughEntry(string fault)
    {
        using var fixture = new InstallerRegistrationFixture();
        var digest = fixture.Digest; var path = fixture.TargetPath;
        if (fault == "digest") digest = new('a', 64);
        if (fault == "schema")
        {
            File.WriteAllBytes(path, "{\"unknown\":true}"u8.ToArray());
            digest = InstallerIdentityFixture.Digest(File.ReadAllBytes(path));
        }
        if (fault == "missing") path += ".missing";
        var result = await fixture.Execute("installer-state", "begin", "--root", fixture.Application,
            "--transaction", fixture.Transaction.ToString("D"), "--manifest", digest, "--manifest-file", path);
        Assert.Equal(1, result.ExitCode);
        Assert.False(Directory.Exists(fixture.Application)); Assert.False(Directory.Exists(fixture.Checkpoint));
    }

    [Fact(DisplayName = "Native Setup Registration Requires Its Frozen Target Even When Generic Metadata Checkpoints Exist")]
    public void RequiresFrozenTarget()
    {
        using var fixture = new InstallerMetadataFixture();
        var store = new InstallerCheckpointStore(fixture.Paths, "test");
        var frame = store.Create(fixture.Application, Guid.NewGuid(), new('a', 64), InstallerRetainedFixture.Parent());
        Assert.Throws<IOException>(() => InstallerTargetManifest.Read(frame));
    }

    private static byte[] Manifest(string root)
    {
        var target = Path.Combine(root, "target");
        ApplicationPayloadSeed.Prepare(target);
        return ApplicationPayloadSeed.Write(target);
    }
}
