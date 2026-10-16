using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using NexusPipeline.Modules.Updates;
using Xunit;
namespace NexusPipeline.Tests.Updates;
public sealed class InstallerToolsTests
{
    [Fact]
    public void RepairUsesRecordedUninstallerBytesAndMissingToolsDoNotAdoptTheHost()
    {
        WithOwner((root, owner) => {
            var tools = owner.InstallerTools!;
            Assert.NotEqual(owner.PayloadFiles.Single().Sha256, tools.Worker.Sha256);
            File.Delete(tools.PublicEntry.Path);
            Assert.Null(InstallationOwnership.RepairInstallerTools(owner));
            Assert.Equal(tools.Worker.Sha256, UpdateApply.ImageHash(tools.PublicEntry.Path));
            File.Delete(tools.PublicEntry.Path); File.Delete(tools.Worker.Path);
            string host = UpdateApply.ImageHash(Path.Combine(root, "NexusPipeline.exe"));
            Assert.Equal("installer.uninstall_requires_setup", InstallationOwnership.RepairInstallerTools(owner));
            Assert.Equal(host, UpdateApply.ImageHash(Path.Combine(root, "NexusPipeline.exe")));
            Assert.False(File.Exists(tools.PublicEntry.Path)); Assert.False(File.Exists(tools.Worker.Path));
            File.WriteAllText(tools.Worker.Path, "dedicated uninstaller fixture");
            File.WriteAllText(tools.PublicEntry.Path, "unknown bytes must remain");
            Assert.Throws<IOException>(() => InstallationOwnership.RepairInstallerTools(owner));
            Assert.Equal("unknown bytes must remain", File.ReadAllText(tools.PublicEntry.Path));
            Assert.Equal(tools.Worker.Sha256, UpdateApply.ImageHash(tools.Worker.Path));
        });
    }
    [Fact]
    public void FixedToolPathsAndUnknownSchemasAreRejectedWithoutRewritingTheProtectedRecord()
    {
        WithOwner((root, owner) => {
            string path = Path.Combine(InstallationOwnership.ManagerDirectory, "identity.protected");
            byte[] original = File.ReadAllBytes(path);
            var read = InstallationOwnership.Read(root, true)!;
            Assert.Equal(2, read.SchemaVersion); Assert.Equal(owner.InstallerTools, read.InstallerTools);
            var outside = owner with { InstallerTools = owner.InstallerTools! with { Worker = owner.InstallerTools.Worker with { Path = Path.Combine(root, "outside.exe") } } };
            Assert.Throws<IOException>(() => InstallationOwnership.Unseal(InstallationOwnership.Seal(outside), root, owner.InstanceId, owner.WindowsUserId));
            var unknown = owner with { SchemaVersion = 3 };
            Assert.Throws<InvalidDataException>(() => InstallationOwnership.Unseal(InstallationOwnership.Seal(unknown), root, owner.InstanceId, owner.WindowsUserId));
            Assert.Equal(original, File.ReadAllBytes(path));
            File.WriteAllText(owner.InstallerTools!.NativeEngine.Path, "unknown native bytes");
            var nativeError = Assert.Throws<IOException>(() => InstallerMetadataCheckpoint.Begin(root, Guid.NewGuid().ToString("N"), "0.17.5", new string('b',64), true));
            Assert.Contains("installer.native_engine_changed", nativeError.Message);
            Assert.Equal("unknown native bytes", File.ReadAllText(owner.InstallerTools.NativeEngine.Path));
            Assert.Equal(original, File.ReadAllBytes(path));
            var legacy = owner with { SchemaVersion = 1, InstallerTools = null };
            string legacySeal = InstallationOwnership.Seal(legacy);
            Assert.Equal(1, InstallationOwnership.Unseal(legacySeal, root, owner.InstanceId, owner.WindowsUserId).SchemaVersion);
            File.WriteAllText(path, legacySeal);
            using (var key = Registry.CurrentUser.OpenSubKey(InstallationOwnership.CurrentRegistryPath, true))
                key!.SetValue("IdentityDigest", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(legacySeal))));
            var rejection = Assert.Throws<IOException>(() => InstallationOwnership.CommitManualAdoption(root, legacy, null!));
            Assert.Contains("installer.tools_migration_requires_setup", rejection.Message);
            Assert.Equal(legacySeal, File.ReadAllText(path));
            Assert.Equal(owner.InstallerTools!.Worker.Sha256, UpdateApply.ImageHash(owner.InstallerTools.Worker.Path));
        });
    }
    [Fact]
    public void MetadataCheckpointRestoresLegacySealToolsAndAnchorAfterEveryRegistrationWrite()
    {
        foreach (string stop in new[] { "worker", "identity", "anchor" }) WithOwner((root, owner) => {
            var legacy = owner with { SchemaVersion = 1, InstallerTools = null };
            string identityPath = Path.Combine(InstallationOwnership.ManagerDirectory, "identity.protected");
            string legacyHelper = Path.Combine(InstallationOwnership.ManagerDirectory, "nexus-installer-helper.exe");
            File.Delete(owner.InstallerTools!.PublicEntry.Path); File.Delete(owner.InstallerTools.Worker.Path);
            File.Copy(Path.Combine(root, "NexusPipeline.exe"), legacyHelper);
            string oldSeal = InstallationOwnership.Seal(legacy);
            File.WriteAllText(identityPath, oldSeal, new UTF8Encoding(true));
            byte[] original = File.ReadAllBytes(identityPath);
            using (var key = Registry.CurrentUser.OpenSubKey(InstallationOwnership.CurrentRegistryPath, true))
                key!.SetValue("IdentityDigest", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(oldSeal))));
            string originalDigest;
            using (var key = Registry.CurrentUser.OpenSubKey(InstallationOwnership.CurrentRegistryPath)) originalDigest = (string)key!.GetValue("IdentityDigest")!;
            string native = owner.InstallerTools.NativeEngine.Path;
            File.WriteAllText(Path.ChangeExtension(native, ".dat"), "native log fixture");
            using (var key = Registry.CurrentUser.CreateSubKey(InstallationOwnership.CurrentUninstallRegistryPath))
            { key.SetValue("DisplayVersion", owner.Version); key.SetValue("UninstallString", "\"" + native + "\""); }
            string transaction = Guid.NewGuid().ToString("N");
            File.WriteAllText(owner.InstallerTools.PublicEntry.Path, "unknown public entry");
            var entryError = Assert.Throws<IOException>(() => InstallerMetadataCheckpoint.Begin(root, transaction, "0.17.5", new string('b',64), true));
            Assert.Contains("installer.unknown_uninstall_entry_preserved", entryError.Message);
            Assert.Equal("unknown public entry", File.ReadAllText(owner.InstallerTools.PublicEntry.Path));
            Assert.Equal(original, File.ReadAllBytes(identityPath));
            File.Delete(owner.InstallerTools.PublicEntry.Path);
            InstallerMetadataCheckpoint.Begin(root, transaction, "0.17.5", new string('b',64), true);
            InstallerMetadataCheckpoint.Preposition(root, transaction);
            using (var key = Registry.CurrentUser.OpenSubKey(InstallationOwnership.CurrentUninstallRegistryPath, true)) key!.SetValue("DisplayVersion", "0.17.5");
            InstallerMetadataCheckpoint.Observe(root, transaction);
            string entry = owner.InstallerTools.PublicEntry.Path;
            byte[] tool = Encoding.UTF8.GetBytes("dedicated target uninstaller fixture");
            string toolHash = Convert.ToHexStringLower(SHA256.HashData(tool));
            var state = LegacyInstallerMetadataCheckpoint.ReadState();
            LegacyInstallerMetadataCheckpoint.Write(state with { After = state.After! with {
                InstallerFiles = state.After!.InstallerFiles!.Append(new(InstallationOwnership.UninstallEntry, toolHash, "app"))
                    .OrderBy(file => (file.Location ?? "manager") + "/" + file.Name, StringComparer.Ordinal).ToArray() } });
            File.WriteAllBytes(entry, tool);
            var target = owner with { Version = "0.17.5", InstallerTools = owner.InstallerTools with {
                PublicEntry = owner.InstallerTools.PublicEntry with { Sha256 = toolHash, Version = "0.17.5" },
                Worker = owner.InstallerTools.Worker with { Sha256 = toolHash, Version = "0.17.5" } } };
            string seal = InstallationOwnership.Seal(target);
            InstallerMetadataCheckpoint.StageInstallerTools(target, seal);
            File.Copy(entry, target.InstallerTools.Worker.Path, true); File.Delete(legacyHelper);
            if (stop is "identity" or "anchor") File.WriteAllText(identityPath, seal, new UTF8Encoding(true));
            if (stop == "anchor") using (var key = Registry.CurrentUser.OpenSubKey(InstallationOwnership.CurrentRegistryPath, true))
                key!.SetValue("IdentityDigest", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seal))));
            Assert.Equal("Restored", InstallerMetadataCheckpoint.RecoverPending(root));
            Assert.Equal(original, File.ReadAllBytes(identityPath));
            var restoredOwner = InstallationOwnership.Read(root)!;
            Assert.Equal(1, restoredOwner.SchemaVersion); Assert.Equal(legacy.InstanceId, restoredOwner.InstanceId); Assert.Equal(legacy.Version, restoredOwner.Version);
            Assert.Equal(owner.PayloadFiles.Single().Sha256, UpdateApply.ImageHash(legacyHelper));
            Assert.False(File.Exists(entry)); Assert.False(File.Exists(target.InstallerTools.Worker.Path));
            using var restored = Registry.CurrentUser.OpenSubKey(InstallationOwnership.CurrentRegistryPath);
            Assert.Equal(originalDigest, restored!.GetValue("IdentityDigest"));
        });
    }
    [Fact]
    public void RetainedDataReinstallDoesNotRequireTheNativeEngineWhichInnoAlreadyRemoved()
    {
        WithOwner((root, owner) => {
            File.Delete(Path.Combine(root, "NexusPipeline.exe"));
            File.Delete(owner.InstallerTools!.PublicEntry.Path); File.Delete(owner.InstallerTools.NativeEngine.Path);
            var retained = owner with { State = "retained-data" };
            string seal = InstallationOwnership.Seal(retained), path = Path.Combine(InstallationOwnership.ManagerDirectory, "identity.protected");
            File.WriteAllText(path, seal, new UTF8Encoding(true));
            byte[] original = File.ReadAllBytes(path);
            using (var key = Registry.CurrentUser.OpenSubKey(InstallationOwnership.CurrentRegistryPath, true))
                key!.SetValue("IdentityDigest", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seal))));
            string transaction = Guid.NewGuid().ToString("N");
            InstallerMetadataCheckpoint.Begin(root, transaction, owner.Version, new string('b',64), false);
            Assert.Equal("Aborted", InstallerMetadataCheckpoint.RecoverPending(root));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal("retained-data", InstallationOwnership.Read(root)!.State);
            Assert.False(File.Exists(owner.InstallerTools.NativeEngine.Path));
        });
    }
    private static void WithOwner(Action<string, InstallerInstanceIdentity> action)
    {
        string id = Guid.NewGuid().ToString("N"), directory = Path.Combine(Path.GetTempPath(), "nxp-tools-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(directory, "app"); Directory.CreateDirectory(root);
        string? savedId = Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE"), savedRoot = Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT");
        Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE", id); Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT", directory);
        File.WriteAllText(Path.Combine(directory, ".nxp-installer-lab"), id);
        try
        {
            Directory.CreateDirectory(InstallationOwnership.ManagerDirectory);
            string host = Path.Combine(root, "NexusPipeline.exe"), entry = Path.Combine(root, InstallationOwnership.UninstallEntry),
                worker = Path.Combine(InstallationOwnership.ManagerDirectory, InstallationOwnership.UninstallWorker), native = Path.Combine(root, "unins000.exe");
            File.WriteAllText(host, "host fixture with different responsibilities");
            File.WriteAllText(entry, "dedicated uninstaller fixture"); File.Copy(entry, worker); File.WriteAllText(native, "native Inno fixture");
            var owner = new InstallerInstanceIdentity(2, id, root, WindowsIdentity.GetCurrent().User!.Value, DateTimeOffset.UtcNow,
                "active", "0.17.4", [new("NexusPipeline.exe", UpdateApply.ImageHash(host))]) { InstallerTools = new(
                    new(entry, UpdateApply.ImageHash(entry), "0.17.4"), new(worker, UpdateApply.ImageHash(worker), "0.17.4"), new(native, UpdateApply.ImageHash(native), "6.7.3")) };
            string seal = InstallationOwnership.Seal(owner);
            File.WriteAllText(Path.Combine(InstallationOwnership.ManagerDirectory, "identity.protected"), seal);
            using (var key = Registry.CurrentUser.CreateSubKey(InstallationOwnership.CurrentRegistryPath))
            { key.SetValue("InstanceId", id); key.SetValue("IdentityDigest", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seal)))); key.SetValue("DataRoot", root); key.SetValue("AppDir", root); }
            action(root, owner);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\NexusPipeline\Tests\Installer\" + id, false);
            Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE", savedId); Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT", savedRoot);
            Directory.Delete(directory, true);
        }
    }
}
