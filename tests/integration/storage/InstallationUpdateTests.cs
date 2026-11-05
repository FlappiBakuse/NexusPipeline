using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using NexusPipeline.Platform.Storage.Maintenance;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallationUpdateTests
{
    [Fact(DisplayName = "Installed Update Source Freezes Original Native Metadata Identity And Runtime Before Any Publication")]
    public void FreezesInstallationSource()
    {
        using var fixture = new InstallationUpdateFixture();
        Assert.NotNull(fixture.Source.Installation);
        Assert.Contains(fixture.Source.Installation, UpdateSourceCodec.References(fixture.Source));
        var original = InstallationUpdateBinding.Identity(fixture.Installation, fixture.Plan.TargetPath);
        Assert.Equal(fixture.Original, original);
        Assert.Equal("0.18.0", Version(fixture));
        Assert.False(Directory.Exists(fixture.Checkpoint));
        _ = new UpdateSourceCodec(fixture.Instance, new()).RequireFrozenBinding(fixture.Source, fixture.Plan);
        new MaintenanceInstallation(fixture.Instance, new()).RequireOriginal(fixture.Source, fixture.Plan, "test");
    }

    [Fact(DisplayName = "Installed Metadata Publication Preserves Native Worker Engine Runtime And Installation Identity While Updating The Bound Manifest")]
    public void PublishesVerifiedMetadata()
    {
        using var fixture = new InstallationUpdateFixture(); fixture.Switch();
        var prepared = fixture.Prepare();
        var runtime = File.ReadAllBytes(Path.Combine(fixture.Instance.RuntimeRoot, "instance.json"));
        new MaintenanceInstallation(fixture.Instance, new()).Publish(fixture.Source, fixture.Plan, "test", prepared.Commit);
        var current = new InstallationAnchor(fixture.Product.Paths.ManagerRoot, fixture.Product.Paths.RegistryKey).Read(fixture.Plan.TargetPath)!;
        Assert.Equal(fixture.Original with { ManifestSha256 = fixture.Source.PayloadManifest.Sha256, ProductVersion = "0.18.1" }, current);
        Assert.Equal("0.18.1", Version(fixture));
        Assert.Equal(runtime, File.ReadAllBytes(Path.Combine(fixture.Instance.RuntimeRoot, "instance.json")));
        Assert.False(Directory.Exists(fixture.Checkpoint));
        new MaintenanceInstallation(fixture.Instance, new()).Publish(fixture.Source, fixture.Plan, "test", prepared.Commit);
    }

    [Fact(DisplayName = "Uncommitted Installation Preparation Cancels Without Changing Original Metadata Or Data")]
    public void CancelsUncommittedMetadata()
    {
        using var fixture = new InstallationUpdateFixture();
        var before = InstallerMetadataInventory.Capture(fixture.Plan.TargetPath, fixture.Product.Paths);
        _ = fixture.Prepare();
        new MaintenanceInstallation(fixture.Instance, new()).CancelPreparation(fixture.Source, fixture.Plan, "test");
        Assert.True(InstallerMetadataInventory.Equal(before, InstallerMetadataInventory.Capture(fixture.Plan.TargetPath, fixture.Product.Paths)));
        Assert.Equal(fixture.Original, new InstallationAnchor(fixture.Product.Paths.ManagerRoot, fixture.Product.Paths.RegistryKey).Read(fixture.Plan.TargetPath));
        Assert.False(Directory.Exists(fixture.Checkpoint));
    }

    [Fact(DisplayName = "Maintenance Publication Refuses A Native Setup Target Added To Its Separately Frozen Checkpoint")]
    public void RejectsSetupTarget()
    {
        using var fixture = new InstallationUpdateFixture(); fixture.Switch();
        var prepared = fixture.Prepare();
        var target = File.ReadAllBytes(Path.Combine(fixture.Instance.RuntimeRoot, fixture.Source.PayloadManifest.RelativePath));
        var checkpoint = InstallerCheckpointCodec.Seal(prepared.Frame.Checkpoint with { TargetManifest = target });
        var path = Path.Combine(fixture.Checkpoint, "identity.pending"); var identity = File.ReadAllBytes(path);
        var record = new InstallationUpdateCommit("nexus.installation.update-commit", 1, checkpoint, identity);
        var reference = new ImmutableBlobStore(fixture.Instance, new()).PersistPlan(
            JsonSerializer.SerializeToUtf8Bytes(record, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Throws<IOException>(() => new InstallationUpdateCheckpoint(fixture.Instance, new())
            .Read(reference, fixture.Installation, fixture.Source, fixture.Plan));
        Assert.Equal(identity, File.ReadAllBytes(path));
        Assert.Equal("0.18.0", Version(fixture));
    }

    [Theory(DisplayName = "Installation Publication Resumes Only Frozen Native Identity And Ownership Prefixes")]
    [InlineData("native")]
    [InlineData("identity")]
    [InlineData("ownership")]
    [InlineData("cleanup")]
    public void ResumesKnownPublication(string prefix)
    {
        using var fixture = new InstallationUpdateFixture(); fixture.Switch();
        var prepared = fixture.Prepare(); var frame = prepared.Frame.Checkpoint;
        var before = InstallerSnapshotCodec.Decode(frame.Before, frame.Root);
        var after = InstallerSnapshotCodec.Decode(frame.After!, frame.Root);
        InstallerRegistryEffects.Restore(fixture.Product.Paths.UninstallRegistryKey, after.Uninstall, before.Uninstall);
        if (prefix != "native")
        {
            var proof = before.Files.Single(file => file.Role == "manager" && file.Name == InstallationAnchor.IdentityFile);
            var backup = new InstallerMetadataBackup(fixture.Product.Paths, new(fixture.Product.Paths, "test"));
            File.Move(Path.Combine(fixture.Product.Paths.ManagerRoot, proof.Name), backup.BackupPath(proof));
            File.Move(Path.Combine(fixture.Checkpoint, "identity.pending"), Path.Combine(fixture.Product.Paths.ManagerRoot, proof.Name));
        }
        if (prefix is "ownership" or "cleanup")
            InstallerRegistryEffects.Restore(fixture.Product.Paths.RegistryKey, after.Ownership, before.Ownership);
        if (prefix == "cleanup")
        {
            File.Delete(Directory.GetFiles(Path.Combine(fixture.Checkpoint, "before")).Single());
            File.Delete(Path.Combine(fixture.Checkpoint, "planned.protected"));
            Directory.Delete(Path.Combine(fixture.Checkpoint, "before"));
        }
        new MaintenanceInstallation(fixture.Instance, new()).Publish(fixture.Source, fixture.Plan, "test", prepared.Commit);
        Assert.Equal("0.18.1", Version(fixture));
        Assert.False(Directory.Exists(fixture.Checkpoint));
        Assert.Equal(prepared.Frame.Identity, new InstallationAnchor(fixture.Product.Paths.ManagerRoot, fixture.Product.Paths.RegistryKey).Read(fixture.Plan.TargetPath));
    }

    [Theory(DisplayName = "Unknown Installation Metadata Preserves The Original Native Version Before Any New Publication")]
    [InlineData("pending")]
    [InlineData("worker")]
    [InlineData("registry")]
    [InlineData("manager")]
    [InlineData("source")]
    public void PreservesUnknownMetadata(string fault)
    {
        using var fixture = new InstallationUpdateFixture(); fixture.Switch();
        var prepared = fixture.Prepare();
        var identity = File.ReadAllBytes(Path.Combine(fixture.Product.Paths.ManagerRoot, InstallationAnchor.IdentityFile));
        if (fault == "pending") File.WriteAllText(Path.Combine(fixture.Checkpoint, "identity.pending"), "Retained unknown pending identity");
        if (fault == "worker") File.WriteAllText(Path.Combine(fixture.Product.Paths.ManagerRoot, InstallationAnchor.WorkerFile), "Retained unknown worker");
        if (fault == "manager") File.WriteAllText(Path.Combine(fixture.Product.Paths.ManagerRoot, "foreign.txt"), "Retained foreign manager data");
        if (fault == "registry")
        {
            using var key = Registry.CurrentUser.OpenSubKey(fixture.Product.Paths.RegistryKey, writable: true);
            key!.SetValue("Unknown", "Retained foreign registry data");
        }
        var source = fixture.Source;
        if (fault == "source") source = source with { Installation = source.PayloadManifest };
        var error = Record.Exception(() => new MaintenanceInstallation(fixture.Instance, new()).Publish(source, fixture.Plan, "test", prepared.Commit));
        Assert.True(error is IOException or JsonException);
        Assert.Equal("0.18.0", Version(fixture));
        Assert.Equal(identity, File.ReadAllBytes(Path.Combine(fixture.Product.Paths.ManagerRoot, InstallationAnchor.IdentityFile)));
    }

    private static string Version(InstallationUpdateFixture fixture)
    {
        using var key = Registry.CurrentUser.OpenSubKey(fixture.Product.Paths.UninstallRegistryKey, writable: false);
        return (string)key!.GetValue("DisplayVersion")!;
    }
}
