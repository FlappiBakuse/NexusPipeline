using System.Runtime.Versioning;
using Microsoft.Win32;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerRegistrationTests
{
    [Fact(DisplayName = "Fresh Installer Registration Publishes A Bound Private Worker Native Engine Runtime And Protected Anchor")]
    public void RegistersNewInstallation()
    {
        using var fixture = new InstallerRegistrationFixture(); fixture.Begin(); fixture.InstallPayload();
        var identity = fixture.Owner.Register(fixture.Application, fixture.Transaction);
        Assert.Equal(identity, new InstallationAnchor(fixture.Paths.ManagerRoot, fixture.Paths.RegistryKey).Read(fixture.Application));
        Assert.Equal(fixture.Digest, identity.ManifestSha256); Assert.Equal("active", identity.State);
        Assert.Equal(PhysicalObjectIdentity.Read(fixture.Application), identity.RootIdentity);
        Assert.Equal(File.ReadAllBytes(fixture.WorkerPath),
            File.ReadAllBytes(Path.Combine(fixture.Paths.ManagerRoot, InstallationAnchor.WorkerFile)));
        Assert.False(Directory.Exists(fixture.Checkpoint));
        Assert.Equal(identity, fixture.Owner.Register(fixture.Application, fixture.Transaction));
        using var payload = InstallationPayload.Open(fixture.Application, fixture.Digest, "test");
    }

    [Theory(DisplayName = "Registration Resumes Only Frozen Physical Objects After A Known Partial Metadata Publication")]
    [InlineData(false)]
    [InlineData(true)]
    public void ResumesPlannedRegistration(bool registryPrefix)
    {
        using var fixture = new InstallerRegistrationFixture(); fixture.Begin(); fixture.InstallPayload();
        fixture.Owner.PrepareRegistration(fixture.Application, fixture.Transaction);
        var store = new InstallerCheckpointStore(fixture.Paths, "test"); var frame = store.Read(fixture.Application, fixture.Transaction);
        Assert.Equal("planned", frame.Stage);
        File.Move(Path.Combine(fixture.Checkpoint, "identity.pending"), Path.Combine(fixture.Paths.ManagerRoot, InstallationAnchor.IdentityFile));
        if (registryPrefix)
        {
            File.Move(Path.Combine(fixture.Checkpoint, "worker.pending"), Path.Combine(fixture.Paths.ManagerRoot, InstallationAnchor.WorkerFile));
            using var key = Registry.CurrentUser.CreateSubKey(fixture.Paths.RegistryKey);
            key.SetValue("ApplicationRoot", fixture.Application, RegistryValueKind.String);
        }
        var identity = fixture.Owner.Register(fixture.Application, fixture.Transaction);
        Assert.Equal(identity, new InstallationAnchor(fixture.Paths.ManagerRoot, fixture.Paths.RegistryKey).Read(fixture.Application));
        Assert.False(Directory.Exists(fixture.Checkpoint));
    }

    [Theory(DisplayName = "Fresh Registration Preserves Unknown Payload Runtime Manager And Native Registration Without Publishing Ownership")]
    [InlineData("payload")]
    [InlineData("runtime")]
    [InlineData("manager")]
    [InlineData("native")]
    public void PreservesForeignState(string fault)
    {
        using var fixture = new InstallerRegistrationFixture(); fixture.Begin(); fixture.InstallPayload();
        var file = Path.Combine(fixture.Application, "README.md");
        if (fault == "payload") File.WriteAllText(file, "Retained changed payload");
        if (fault == "runtime") { Directory.CreateDirectory(Path.Combine(fixture.Application, "runtime")); file = Path.Combine(fixture.Application, "runtime/user.json"); File.WriteAllText(file, "Retained unknown runtime"); }
        if (fault == "manager") { file = Path.Combine(fixture.Paths.ManagerRoot, "foreign.txt"); File.WriteAllText(file, "Retained unknown manager data"); }
        if (fault == "native")
        {
            using var key = Registry.CurrentUser.OpenSubKey(fixture.Paths.UninstallRegistryKey, writable: true);
            key!.SetValue("InstallLocation", Path.Combine(fixture.Root, "foreign"));
        }
        var bytes = File.ReadAllBytes(file); var original = PhysicalObjectIdentity.Read(file);
        Assert.Throws<IOException>(() => fixture.Owner.Register(fixture.Application, fixture.Transaction));
        Assert.Equal(bytes, File.ReadAllBytes(file)); Assert.Equal(original, PhysicalObjectIdentity.Read(file));
        Assert.False(InstallerMetadataInventory.CaptureRegistry(fixture.Paths.RegistryKey).Exists);
        Assert.False(File.Exists(Path.Combine(fixture.Paths.ManagerRoot, InstallationAnchor.IdentityFile)));
        Assert.True(File.Exists(Path.Combine(fixture.Checkpoint, "prepared.protected")));
    }

    [Fact(DisplayName = "Registration Preflights Every Planned Worker Before Moving The New Protected Identity")]
    public void PreservesChangedPlan()
    {
        using var fixture = new InstallerRegistrationFixture(); fixture.Begin(); fixture.InstallPayload();
        fixture.Owner.PrepareRegistration(fixture.Application, fixture.Transaction);
        var worker = Path.Combine(fixture.Checkpoint, "worker.pending"); File.WriteAllBytes(worker, [7, 11, 17]);
        var identity = Path.Combine(fixture.Checkpoint, "identity.pending"); var bytes = File.ReadAllBytes(identity);
        Assert.Throws<IOException>(() => fixture.Owner.Register(fixture.Application, fixture.Transaction));
        Assert.Equal(bytes, File.ReadAllBytes(identity)); Assert.Equal(new byte[] { 7, 11, 17 }, File.ReadAllBytes(worker));
        Assert.False(File.Exists(Path.Combine(fixture.Paths.ManagerRoot, InstallationAnchor.IdentityFile)));
        Assert.False(InstallerMetadataInventory.CaptureRegistry(fixture.Paths.RegistryKey).Exists);
    }

    [Fact(DisplayName = "The Actual Maintenance Entry Completes A Fresh Native Metadata Registration And Reads Its Owned Destination")]
    public async Task RegistersThroughEntry()
    {
        using var fixture = new InstallerRegistrationFixture();
        var begun = await fixture.Execute("installer-state", "begin", "--root", fixture.Application,
            "--transaction", fixture.Transaction.ToString("D"), "--manifest", fixture.Digest, "--manifest-file", fixture.TargetPath);
        Assert.Equal(0, begun.ExitCode);
        Assert.Equal("installation", new InstallerCheckpointStore(fixture.Paths, "test").Read(fixture.Application, fixture.Transaction).Scope);
        Assert.Equal(0, (await fixture.Execute("installer-state", "begin", "--root", fixture.Application,
            "--transaction", fixture.Transaction.ToString("D"), "--manifest", fixture.Digest, "--manifest-file", fixture.TargetPath)).ExitCode);
        fixture.InstallPayload();
        var result = await fixture.Execute("installer-state", "register", "--root", fixture.Application, "--transaction", fixture.Transaction.ToString("D"));
        Assert.Equal(0, result.ExitCode);
        using var reply = System.Text.Json.JsonDocument.Parse(result.Output);
        Assert.Equal("nexus.installer.registration", reply.RootElement.GetProperty("format").GetString());
        var replay = await fixture.Execute("installer-state", "register", "--root", fixture.Application, "--transaction", fixture.Transaction.ToString("D"));
        Assert.Equal(0, replay.ExitCode); Assert.Equal(result.Output, replay.Output);
        var owned = await fixture.Execute("installer-state", "inspect", "--root", fixture.Application);
        Assert.Equal(0, owned.ExitCode);
        using var inspected = System.Text.Json.JsonDocument.Parse(owned.Output);
        Assert.Equal("owned", inspected.RootElement.GetProperty("kind").GetString());
        Assert.False(Directory.Exists(fixture.Checkpoint));
    }

    [Theory(DisplayName = "Prepared Cancellation Removes Only Empty Owned Metadata And Preserves Unknown Destination Bytes")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelsThroughEntry(bool foreign)
    {
        using var fixture = new InstallerRegistrationFixture();
        Assert.Equal(0, (await fixture.Execute("installer-state", "begin", "--root", fixture.Application,
            "--transaction", fixture.Transaction.ToString("D"), "--manifest", fixture.Digest, "--manifest-file", fixture.TargetPath)).ExitCode);
        var file = Path.Combine(fixture.Application, "foreign.txt");
        if (foreign) File.WriteAllText(file, "Retained foreign destination data");
        Assert.Equal(foreign ? 1 : 0, (await fixture.Execute("installer-state", "cancel-prepared", "--root", fixture.Application,
            "--transaction", fixture.Transaction.ToString("D"))).ExitCode);
        Assert.Equal(foreign, Directory.Exists(fixture.Paths.ManagerRoot));
        Assert.False(InstallerMetadataInventory.CaptureRegistry(fixture.Paths.RegistryKey).Exists);
        if (foreign) Assert.Equal("Retained foreign destination data", File.ReadAllText(file));
        else Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Application));
    }
}
