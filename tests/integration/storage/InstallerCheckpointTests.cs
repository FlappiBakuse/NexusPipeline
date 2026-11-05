using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerCheckpointTests
{
    [Fact(DisplayName = "Native Installer Checkpoints Bind Before After Source Actors And The Exact Protected Predecessor")]
    public void FreezesNativeCheckpoint()
    {
        using var fixture = new InstallerMetadataFixture();
        var store = new InstallerCheckpointStore(fixture.Paths, "test"); var transaction = Guid.NewGuid();
        var created = store.Create(fixture.Application, transaction, new('a', 64), Parent());
        var prepared = store.Read(fixture.Application, transaction);
        Assert.Equal("prepared", prepared.Stage); Assert.Equal(created.TransactionId, prepared.TransactionId);
        Assert.Equal("installation", prepared.Scope);
        Assert.Equal(created.Before, prepared.Before); Assert.Null(prepared.After);
        using (var key = Registry.CurrentUser.OpenSubKey(fixture.Paths.UninstallRegistryKey, writable: true))
            key!.SetValue("DisplayVersion", "0.18.0");
        var observed = store.Observe(fixture.Application, transaction);
        Assert.Equal("observed", observed.Stage); Assert.NotNull(observed.After);
        var path = Path.Combine(fixture.Paths.ManagerRoot, "metadata-checkpoint", "observed.protected");
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(observed.After, store.Read(fixture.Application, transaction).After);
        Assert.Equal(observed.After, store.Observe(fixture.Application, transaction).After);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(InstallerMetadataInventory.Equal(InstallerSnapshotCodec.Decode(observed.Before, fixture.Application),
            InstallerSnapshotCodec.Decode(observed.After!, fixture.Application)));
    }

    [Fact(DisplayName = "A New Checkpoint Records A Missing Metadata Manager Before Creating Its Own Protected Workspace")]
    public void RecordsMissingManager()
    {
        using var fixture = new InstallerMetadataFixture(seed: false); Directory.CreateDirectory(fixture.Application);
        var store = new InstallerCheckpointStore(fixture.Paths, "test"); var transaction = Guid.NewGuid();
        var created = store.Create(fixture.Application, transaction, new('a', 64), Parent());
        var original = InstallerSnapshotCodec.Decode(created.Before, fixture.Application);
        Assert.Null(original.Directories.Single(directory => directory.Role == "manager").Identity);
        Assert.Equal(created.ManagerIdentity, PhysicalObjectIdentity.Read(fixture.Paths.ManagerRoot));
        Assert.Equal(created.Before, store.Read(fixture.Application, transaction).Before);
    }

    [Fact(DisplayName = "Installer Recovery Refuses To Compensate While The Original Setup Or Metadata Writer Is Still Live")]
    public void RequiresStoppedWriters()
    {
        using var fixture = new InstallerMetadataFixture();
        var store = new InstallerCheckpointStore(fixture.Paths, "test"); var transaction = Guid.NewGuid();
        store.Create(fixture.Application, transaction, new('a', 64), Parent());
        var path = Path.Combine(fixture.Paths.ManagerRoot, "metadata-checkpoint", "prepared.protected");
        var bytes = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => store.RequireStopped(fixture.Application, transaction));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory(DisplayName = "Installer Checkpoint Identity Rejects Another Root Transaction Or Build Profile")]
    [InlineData("root")]
    [InlineData("transaction")]
    [InlineData("profile")]
    [InlineData("scope")]
    public void RejectsForeignContext(string fault)
    {
        using var fixture = new InstallerMetadataFixture();
        var store = new InstallerCheckpointStore(fixture.Paths, "test"); var transaction = Guid.NewGuid();
        store.Create(fixture.Application, transaction, new('a', 64), Parent());
        if (fault == "profile") store = new(fixture.Paths, "production");
        if (fault == "scope") store = new(fixture.Paths, "test", "ownership");
        Assert.Throws<IOException>(() => store.Read(fault == "root" ? Path.Combine(fixture.Root, "other") : fixture.Application,
            fault == "transaction" ? Guid.NewGuid() : transaction));
    }

    [Theory(DisplayName = "Unknown Corrupt And Replaced Installer Checkpoints Stay In Place For Recovery")]
    [InlineData("ciphertext")]
    [InlineData("format")]
    [InlineData("scope")]
    [InlineData("numeric-string")]
    [InlineData("extra")]
    [InlineData("member")]
    [InlineData("directory")]
    [InlineData("replacement")]
    public void PreservesUnknownCheckpoint(string fault)
    {
        using var fixture = new InstallerMetadataFixture();
        var store = new InstallerCheckpointStore(fixture.Paths, "test"); var transaction = Guid.NewGuid();
        var frame = store.Create(fixture.Application, transaction, new('a', 64), Parent());
        var directory = Path.Combine(fixture.Paths.ManagerRoot, "metadata-checkpoint");
        var path = Path.Combine(directory, "prepared.protected");
        if (fault is "format" or "scope" or "numeric-string" or "extra")
        {
            var value = JsonSerializer.SerializeToNode(frame, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            if (fault == "format") value["format"] = "other.checkpoint";
            if (fault == "scope") value["scope"] = "unknown";
            if (fault == "numeric-string") value["version"] = "1";
            if (fault == "extra") value["foreign"] = true;
            File.WriteAllBytes(path, WindowsDataProtection.Transform(JsonSerializer.SerializeToUtf8Bytes(value),
                Encoding.UTF8.GetBytes("nexus.installer.checkpoint/g0180/1"), protect: true));
        }
        if (fault == "ciphertext") File.WriteAllBytes(path, [1, 2, 3]);
        if (fault == "member") File.WriteAllText(Path.Combine(directory, "foreign.txt"), "Retained foreign bytes");
        if (fault == "directory") Directory.CreateDirectory(Path.Combine(directory, "observed.protected"));
        if (fault == "replacement")
        {
            Directory.Move(directory, Path.Combine(fixture.Root, "retained-checkpoint")); Directory.CreateDirectory(directory);
            File.Copy(Path.Combine(fixture.Root, "retained-checkpoint", "prepared.protected"), path);
        }
        var bytes = File.ReadAllBytes(path);
        var error = Record.Exception(() => store.Read(fixture.Application, transaction));
        if (fault is "numeric-string" or "extra") Assert.IsType<JsonException>(error);
        else Assert.IsAssignableFrom<IOException>(error);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "An Authenticated Observed Frame Cannot Be Reattached To A Different Protected Predecessor")]
    public void BindsObservedFrame()
    {
        using var fixture = new InstallerMetadataFixture();
        var store = new InstallerCheckpointStore(fixture.Paths, "test"); var transaction = Guid.NewGuid();
        store.Create(fixture.Application, transaction, new('a', 64), Parent());
        var observed = store.Observe(fixture.Application, transaction);
        var path = Path.Combine(fixture.Paths.ManagerRoot, "metadata-checkpoint", "observed.protected");
        var bytes = InstallerCheckpointCodec.Seal(observed with { ParentSha256 = new('b', 64) });
        File.WriteAllBytes(path, bytes);
        Assert.Throws<IOException>(() => store.Read(fixture.Application, transaction));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "An Installer Observation Retry Rejects Metadata Changed After Its Frozen After Snapshot")]
    public void RejectsChangedObservation()
    {
        using var fixture = new InstallerMetadataFixture();
        var store = new InstallerCheckpointStore(fixture.Paths, "test"); var transaction = Guid.NewGuid();
        store.Create(fixture.Application, transaction, new('a', 64), Parent());
        store.Observe(fixture.Application, transaction);
        File.WriteAllBytes(fixture.Engine, [1, 4, 7]);
        Assert.Throws<IOException>(() => store.Observe(fixture.Application, transaction));
        Assert.Equal(new byte[] { 1, 4, 7 }, File.ReadAllBytes(fixture.Engine));
    }

    private static ProcessIdentity Parent()
    {
        var observed = ProcessSnapshotProbe.Capture(Environment.ProcessId, "", default);
        Assert.False(observed.Truncated);
        return ProcessIdentityReader.Read(Assert.Single(observed.Processes).Ppid);
    }
}
