using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerOwnedTests
{
    [Theory(DisplayName = "Owned Setup Preflight Freezes The Verified Application And Native Source While Preserving Runtime Bytes")]
    [InlineData(false)]
    [InlineData(true)]
    public void FreezesOwnedSource(bool initialized)
    {
        using var fixture = new OwnedFixture(initialized);
        var runtimeBefore = fixture.Scanner.Capture(fixture.Runtime.RuntimeRoot, default);
        var nativeBefore = InstallerMetadataInventory.Capture(fixture.Product.Application, fixture.Product.Paths);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        var source = fixture.Read(source =>
        {
            Assert.Equal(fixture.Identity, source.Identity);
            Assert.True(InstallerMetadataInventory.Equal(nativeBefore, source.Metadata));
            Assert.Equal(FileObjectKind.Directory, source.Application.Shape);
            Assert.Equal(source.Identity.RootIdentity, source.ApplicationObjects.Single(item => item.RelativePath.Length == 0).Identity);
            Assert.Contains(source.Application.Entries, item => item.RelativePath == ApplicationTree.ManifestPath && item.Sha256 == source.Identity.ManifestSha256);
            Assert.DoesNotContain(source.Application.Entries, item => item.RelativePath == "runtime" || item.RelativePath.StartsWith("runtime/", StringComparison.Ordinal));
            Assert.Throws<IOException>(() => File.AppendAllText(Path.Combine(fixture.Product.Application, "README.md"), "changed"));
            return source;
        });
        Assert.Equal("nexus.installer.owned-source", source.Format);
        Assert.Equal(1, source.Version);
        Assert.Equal("test", source.Profile);
        Assert.True(runtimeBefore.Entries.SequenceEqual(fixture.Scanner.Capture(fixture.Runtime.RuntimeRoot, default).Entries));
        Assert.True(InstallerMetadataInventory.Equal(nativeBefore, InstallerMetadataInventory.Capture(fixture.Product.Application, fixture.Product.Paths)));
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
    }

    [Theory(DisplayName = "Owned Setup Preflight Preserves Changed Payload Metadata Runtime And Manager Evidence")]
    [InlineData("payload")]
    [InlineData("metadata")]
    [InlineData("runtime")]
    [InlineData("manager")]
    [InlineData("root")]
    public void PreservesChangedSource(string fault)
    {
        using var fixture = new OwnedFixture(initialized: false);
        var evidence = Path.Combine(fixture.Product.Application, "README.md");
        var runtimeData = Path.Combine(fixture.Runtime.RuntimeRoot, "unknown.bin");
        var managerData = Path.Combine(fixture.Product.Paths.ManagerRoot, "unknown.bin");
        if (fault == "payload") File.AppendAllText(evidence, "Unrecognized application bytes");
        if (fault == "metadata")
        {
            using var key = Registry.CurrentUser.OpenSubKey(fixture.Product.Paths.UninstallRegistryKey, writable: true);
            key!.SetValue("DisplayVersion", "0.17.6", RegistryValueKind.String);
        }
        if (fault == "runtime")
            File.WriteAllBytes(runtimeData, "Preserve runtime evidence"u8.ToArray());
        if (fault == "manager")
            File.WriteAllBytes(managerData, "Preserve manager evidence"u8.ToArray());
        var retainedRoot = fixture.Product.Application + "-preserved";
        if (fault == "root")
        {
            Directory.Move(fixture.Product.Application, retainedRoot);
            Directory.CreateDirectory(fixture.Product.Application);
            evidence = Path.Combine(retainedRoot, "README.md");
        }
        var bytes = File.ReadAllBytes(evidence);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        var applicationRoot = PhysicalObjectIdentity.Read(fixture.Product.Application);
        var files = RuntimeFingerprint.CaptureFileEvidence(evidence, runtimeData, managerData);
        var registration = JsonSerializer.Serialize(InstallerMetadataInventory.CaptureRegistry(fixture.Product.Paths.UninstallRegistryKey));
        Assert.Throws<IOException>(() => fixture.Read(source => source));
        Assert.Equal(bytes, File.ReadAllBytes(evidence));
        Assert.Equal(files, RuntimeFingerprint.CaptureFileEvidence(evidence, runtimeData, managerData));
        Assert.Equal(registration, JsonSerializer.Serialize(InstallerMetadataInventory.CaptureRegistry(fixture.Product.Paths.UninstallRegistryKey)));
        Assert.Equal(applicationRoot, PhysicalObjectIdentity.Read(fixture.Product.Application));
        if (fault == "root")
        {
            Assert.NotEqual(fixture.Identity.RootIdentity, applicationRoot);
            Assert.Equal(fixture.Identity.RootIdentity, PhysicalObjectIdentity.Read(retainedRoot));
        }
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
    }

    [Fact(DisplayName = "Owned Setup Source Inspection Requires Exclusive Runtime Admission Before Returning A Frozen Source")]
    public void RequiresStoppedRuntime()
    {
        using var fixture = new OwnedFixture(initialized: true);
        using var running = InstanceLease.Acquire(fixture.Runtime, new());
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        Assert.Throws<IOException>(() => fixture.Read(source => source));
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
    }

    [Theory(DisplayName = "Owned Setup Diagnostic Copies Cannot Be Written Into Application Runtime Or Installer Ownership Directories")]
    [InlineData("application")]
    [InlineData("runtime")]
    [InlineData("manager")]
    public void RequiresExternalInspection(string target)
    {
        using var fixture = new OwnedFixture(initialized: false);
        var reserved = target switch
        {
            "application" => fixture.Product.Application, "runtime" => fixture.Runtime.RuntimeRoot,
            "manager" => fixture.Product.Paths.ManagerRoot, _ => throw new ArgumentException(target)
        };
        var work = Path.Combine(reserved, "inspection");
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        Assert.Throws<IOException>(() => fixture.Owner.Use(fixture.Product.Application, InstallerRetainedFixture.Parent(), work, source => source));
        Assert.False(Directory.Exists(work));
    }

    [Fact(DisplayName = "The Actual Per User Installer Worker Returns The Fully Verified Owned Source Through Its Read Only Transition Entry")]
    public async Task InspectsThroughEntry()
    {
        using var fixture = new OwnedFixture(initialized: true);
        var before = fixture.Scanner.Capture(fixture.Runtime.RuntimeRoot, default);
        string[] files = ["NexusPipeline.exe", "NexusPipeline.update.exe", "README.md",
            "resources/desktop/NexusPipeline.Desktop.exe", "resources/desktop/locales/en-US.pak",
            "resources/desktop/locales/zh-CN.pak", "resources/desktop/resources/app.asar", "resources/payload-manifest.json"];
        string[] dirs = ["", "resources", "resources/desktop", "resources/desktop/locales", "resources/desktop/resources"];
        var identities = files.Concat(dirs).ToDictionary(path => path,
            path => PhysicalObjectIdentity.Read(Path.Combine(fixture.Product.Application, path)), StringComparer.Ordinal);
        string Digest(string path)
        {
            using var file = File.OpenRead(Path.Combine(fixture.Product.Application, path));
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file));
        }
        var digests = files.ToDictionary(path => path, Digest, StringComparer.Ordinal);
        var result = await fixture.Product.Execute("installer-state", "check-transition", "--root", fixture.Product.Application,
            "--work-root", Path.Combine(fixture.Product.Root, "entry-inspection"));
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var source = document.RootElement;
        Assert.Equal("nexus.installer.owned-source", source.GetProperty("format").GetString());
        Assert.Equal(fixture.Identity.InstallationId, source.GetProperty("identity").GetProperty("installationId").GetGuid());
        Assert.Equal(fixture.Identity.RuntimeId, source.GetProperty("identity").GetProperty("runtimeId").GetGuid());
        Assert.Equal("active", source.GetProperty("identity").GetProperty("state").GetString());
        var objects = source.GetProperty("applicationObjects").EnumerateArray().ToArray();
        Assert.Equal(files.Concat(dirs).Order(StringComparer.Ordinal),
            objects.Select(item => item.GetProperty("relativePath").GetString()).Order(StringComparer.Ordinal));
        foreach (var item in objects)
        {
            var relative = item.GetProperty("relativePath").GetString()!;
            Assert.Equal(identities[relative],
                item.GetProperty("identity").Deserialize<PhysicalObjectIdentity>(JsonSerializerOptions.Web));
            Assert.Equal(identities[relative], PhysicalObjectIdentity.Read(Path.Combine(fixture.Product.Application, relative)));
        }
        var entries = source.GetProperty("application").GetProperty("entries").EnumerateArray().ToArray();
        Assert.All(digests, expected =>
        {
            var entry = Assert.Single(entries, item => item.GetProperty("relativePath").GetString() == expected.Key);
            Assert.Equal(expected.Value, entry.GetProperty("sha256").GetString());
            Assert.Equal(expected.Value, Digest(expected.Key));
        });
        Assert.True(before.Entries.SequenceEqual(fixture.Scanner.Capture(fixture.Runtime.RuntimeRoot, default).Entries));
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
    }

    internal sealed class OwnedFixture : IDisposable
    {
        internal InstallerRegistrationFixture Product { get; } = new();
        internal FileTreeScanner Scanner { get; } = new(SnapshotBudget.Default);
        internal InstallationIdentity Identity { get; }
        internal RuntimeInstance Runtime { get; }
        internal InstallerOwnedRuntime Owner => new(Product.Paths, "test");

        internal OwnedFixture(bool initialized)
        {
            Product.Begin(); Product.InstallPayload();
            Identity = Product.Owner.Register(Product.Application, Product.Transaction);
            Runtime = new InstanceRoot(new()).OpenExisting(Path.Combine(Product.Application, "runtime"));
            if (!initialized) return;
            using var lease = InstanceLease.Acquire(Runtime, new());
            var database = StateDatabase.CreateFresh(Runtime);
            _ = new StartupJournalRecovery(Runtime, new(), database,
                new(Product.Application, Path.Combine(Runtime.RuntimeRoot, "extensions"))).Recover(lease);
            File.WriteAllBytes(Path.Combine(Runtime.RuntimeRoot, "user-data.bin"), "Keep owned accounts and configuration"u8.ToArray());
        }

        internal TResult Read<TResult>(Func<InstallerOwnedSource, TResult> read) => Owner.Use(Product.Application,
            InstallerRetainedFixture.Parent(), Path.Combine(Product.Root, "inspection"), read);

        public void Dispose() => Product.Dispose();
    }
}
