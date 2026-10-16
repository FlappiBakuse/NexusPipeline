using NexusPipeline.ControlPlane.Contracts;
using NexusPipeline.ControlPlane.Cli;
using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.Win32;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.Tests.Updates;

public sealed class InstallationGenerationTests
{
    [Fact]
    public void ProtectedInventoryPreservesLegacyUninstallerPathsAndOwnsTheUpdateEntry()
    {
        string root = Path.Combine(Path.GetTempPath(), "identity-contract");
        var files = new[] { new InstalledPayloadFile("NexusPipeline.exe", new('a', 64)),
            new InstalledPayloadFile(UpdateRecipe.UpdateEntry, new('a', 64)),
            new InstalledPayloadFile(ApplicationPayload.ManifestPath, new('b', 64)) };
        var identity = new InstallerInstanceIdentity(1, Guid.NewGuid().ToString("N"), root, "fixture-user",
            DateTimeOffset.UtcNow, "active", "0.17.4", files);
        string seal = InstallationOwnership.Seal(identity);
        var wire = JsonNode.Parse(System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(seal),
            null, System.Security.Cryptography.DataProtectionScope.CurrentUser))!.AsObject();
        Assert.DoesNotContain(wire["PayloadFiles"]!.AsArray(), file => file!["Path"]!.GetValue<string>() == UpdateRecipe.UpdateEntry);
        Assert.Equal(UpdateRecipe.UpdateEntry, wire["AuxiliaryPayloadFiles"]![0]!["Path"]!.GetValue<string>());
        var restored = InstallationOwnership.Unseal(seal, root, identity.InstanceId, identity.WindowsUserId);
        Assert.Equal(files.OrderBy(file => file.Path, StringComparer.Ordinal), restored.PayloadFiles);
        Assert.Null(restored.AuxiliaryPayloadFiles);
        string RawSeal(JsonObject value) => Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(value.ToJsonString()), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
        var duplicate = wire.DeepClone().AsObject();
        duplicate["PayloadFiles"]!.AsArray().Add(duplicate["AuxiliaryPayloadFiles"]![0]!.DeepClone());
        Assert.Throws<InvalidDataException>(() => InstallationOwnership.Unseal(RawSeal(duplicate), root, identity.InstanceId, identity.WindowsUserId));
        var unknown = wire.DeepClone().AsObject();
        unknown["AuxiliaryPayloadFiles"]![0]!["Path"] = "unknown.exe";
        Assert.Throws<InvalidDataException>(() => InstallationOwnership.Unseal(RawSeal(unknown), root, identity.InstanceId, identity.WindowsUserId));
        var old = identity with { Version = "0.17.3", PayloadFiles = files.Where(file => file.Path != UpdateRecipe.UpdateEntry).ToArray() };
        Assert.Equal(old.PayloadFiles, InstallationOwnership.Unseal(InstallationOwnership.Seal(old), root, old.InstanceId, old.WindowsUserId).PayloadFiles);
    }

    [Fact]
    public void RegisterAndUninstallUseOnlyOwnedGenerationMetadata()
    {
        string id = Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "nxp-installer-generation-" + id);
        string app = Path.Combine(root, "app"), old = Path.Combine(root, "old-app");
        string oldKey = @"Software\NexusPipeline\Tests\LegacyInstaller\" + id;
        string currentKey = @"Software\NexusPipeline\Tests\Installer\" + id;
        string? savedId = Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE");
        string? savedRoot = Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT");
        Directory.CreateDirectory(app); Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(root, ".nxp-installer-lab"), id);
        File.WriteAllText(Path.Combine(old, "NexusPipeline.exe"), "old image remains");
        Directory.CreateDirectory(Path.Combine(root, "old-manager"));
        File.WriteAllText(Path.Combine(root, "old-manager", "identity.protected"), "old identity remains");
        using (var key = Registry.CurrentUser.CreateSubKey(oldKey))
        {
            key.SetValue("DataRoot", old);
            key.SetValue("IdentityDigest", "old registration remains");
        }
        Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE", id);
        Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT", root);
        try
        {
            Assert.Throws<IOException>(() => InstallationOwnership.Read(old));
            Assert.Throws<IOException>(() => InstallationOwnership.Read(root));
            Assert.False(Directory.Exists(InstallationOwnership.ManagerDirectory));
            string image = Path.Combine(app, "NexusPipeline.exe");
            File.WriteAllText(image, "new image");
            string desktop = Path.Combine(app, "resources", "desktop", "locales", "zh-CN.pak");
            Directory.CreateDirectory(Path.GetDirectoryName(desktop)!);
            File.WriteAllText(desktop, "owned desktop locale");
            string inventory = Path.Combine(root, "manifest.json");
            File.WriteAllText(inventory, JsonSerializer.Serialize(new[] {
                new InstalledPayloadFile("NexusPipeline.exe", UpdateApply.ImageHash(image)),
                new InstalledPayloadFile("resources/desktop/locales/zh-CN.pak", UpdateApply.ImageHash(desktop)) }));
            Assert.ThrowsAny<IOException>(() => InstallationOwnership.Register(app, "0.17.0", inventory));
            Assert.False(Directory.Exists(InstallationOwnership.ManagerDirectory));
            Assert.Equal("new image", File.ReadAllText(image));
            InstallationOwnership.RegisterOwnedInventoryForTest(app, "0.17.0", inventory,
                [new InstalledPayloadFile("NexusPipeline.exe", UpdateApply.ImageHash(image)),
                 new InstalledPayloadFile("resources/desktop/locales/zh-CN.pak", UpdateApply.ImageHash(desktop))]);
            Assert.Equal("active", InstallationOwnership.Read(app)!.State);
            Directory.CreateDirectory(Path.Combine(app, "data"));
            string data = Path.Combine(app, "data", "user.json");
            File.WriteAllText(data, "preserved data");
            File.WriteAllText(image, "manually copied image");
            Assert.Throws<IOException>(() => InstallationOwnership.Uninstall(app, false));
            Assert.Equal("manually copied image", File.ReadAllText(image));
            Assert.Equal("preserved data", File.ReadAllText(data));
            Assert.Equal("active", InstallationOwnership.Read(app)!.State);
            File.WriteAllText(image, "new image");
            InstallationOwnership.Uninstall(app, false);
            Assert.False(File.Exists(image));
            Assert.False(Directory.Exists(Path.Combine(app, "resources")));
            Assert.Equal("preserved data", File.ReadAllText(data));
            Assert.Equal("retained-data", InstallationOwnership.Read(app)!.State);
            File.WriteAllText(image, "reinstalled image");
            Directory.CreateDirectory(Path.GetDirectoryName(desktop)!);
            File.WriteAllText(desktop, "reinstalled desktop locale");
            string unknown = Path.Combine(app, "resources", "desktop", "custom.txt");
            File.WriteAllText(unknown, "unowned resource remains");
            File.WriteAllText(inventory, JsonSerializer.Serialize(new[] {
                new InstalledPayloadFile("NexusPipeline.exe", UpdateApply.ImageHash(image)),
                new InstalledPayloadFile("resources/desktop/locales/zh-CN.pak", UpdateApply.ImageHash(desktop)) }));
            InstallationOwnership.RegisterOwnedInventoryForTest(app, "0.17.0", inventory,
                [new InstalledPayloadFile("NexusPipeline.exe", UpdateApply.ImageHash(image)),
                 new InstalledPayloadFile("resources/desktop/locales/zh-CN.pak", UpdateApply.ImageHash(desktop))]);
            InstallationOwnership.Uninstall(app, true);
            Assert.False(File.Exists(image));
            Assert.False(Directory.Exists(Path.Combine(app, "data")));
            Assert.False(Directory.Exists(Path.GetDirectoryName(desktop)!));
            Assert.Equal("unowned resource remains", File.ReadAllText(unknown));
            Assert.Null(InstallationOwnership.Read(app));
            Assert.False(File.Exists(Path.Combine(InstallationOwnership.ManagerDirectory, "identity.protected")));
            using (var deleted = Registry.CurrentUser.OpenSubKey(currentKey)) Assert.Null(deleted);
            Assert.Equal("old image remains", File.ReadAllText(Path.Combine(old, "NexusPipeline.exe")));
            Assert.Equal("old identity remains", File.ReadAllText(Path.Combine(root, "old-manager", "identity.protected")));
            using var retained = Registry.CurrentUser.OpenSubKey(oldKey);
            Assert.Equal("old registration remains", retained!.GetValue("IdentityDigest"));
            Assert.Equal(old, retained.GetValue("DataRoot"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE", savedId);
            Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT", savedRoot);
            Registry.CurrentUser.DeleteSubKeyTree(currentKey, false);
            Registry.CurrentUser.DeleteSubKeyTree(oldKey, false);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AbandonedRegistrationReleasesOnlyEmptyUninstalledRootsAndPreservesTheAnchor()
    {
        foreach (bool sameRoot in new[] { true, false })
        {
            string id = Guid.NewGuid().ToString("N");
            string root = Path.Combine(Path.GetTempPath(), "nxp-abandoned-registration-" + id);
            string previous = Path.Combine(root, "previous"), target = sameRoot ? previous : Path.Combine(root, "new");
            string keyPath = @"Software\NexusPipeline\Tests\Installer\" + id;
            string uninstallPath = @"Software\NexusPipeline\Tests\Uninstall\" + id;
            string? savedId = Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE");
            string? savedRoot = Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT");
            Directory.CreateDirectory(previous); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(root, ".nxp-installer-lab"), id);
            Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE", id);
            Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT", root);
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
                {
                    key.SetValue("DataRoot", previous);
                    key.SetValue("InstanceId", id);
                    key.SetValue("IdentityDigest", new string('a', 64));
                }
                string unknown = Path.Combine(previous, "unknown.txt");
                File.WriteAllText(unknown, "must remain");
                Assert.False(InstallationOwnership.ReleaseAbandonedEmptyRegistration(target));
                Assert.Equal("must remain", File.ReadAllText(unknown));
                File.Delete(unknown);
                Directory.CreateDirectory(InstallationOwnership.ManagerDirectory);
                string unknownMetadata = Path.Combine(InstallationOwnership.ManagerDirectory, "unknown.bin");
                File.WriteAllText(unknownMetadata, "metadata remains");
                Assert.False(InstallationOwnership.ReleaseAbandonedEmptyRegistration(target));
                Assert.Equal("metadata remains", File.ReadAllText(unknownMetadata));
                File.Delete(unknownMetadata);
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath, true)) key!.SetValue("AppDir", previous);
                Assert.False(InstallationOwnership.ReleaseAbandonedEmptyRegistration(target));
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath, true)) key!.DeleteValue("AppDir");
                using (var key = Registry.CurrentUser.CreateSubKey(uninstallPath)) key.SetValue("DisplayName", "existing registration");
                Assert.False(InstallationOwnership.ReleaseAbandonedEmptyRegistration(target));
                Registry.CurrentUser.DeleteSubKey(uninstallPath);
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath, true)) key!.SetValue("Unknown", "preserve");
                Assert.False(InstallationOwnership.ReleaseAbandonedEmptyRegistration(target));
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath, true)) key!.DeleteValue("Unknown");
                Assert.True(InstallationOwnership.HasAbandonedEmptyRegistration());
                Assert.Null(InstallationOwnership.Read(target));
                Assert.True(InstallationOwnership.ReleaseAbandonedEmptyRegistration(target));
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath)) Assert.Null(key);
                string recovery = Assert.Single(Directory.GetFiles(Path.Combine(root, "registration-recovery")));
                string archived = System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(File.ReadAllText(recovery)), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
                using var document = JsonDocument.Parse(archived);
                Assert.Equal(previous, document.RootElement.GetProperty("DataRoot").GetProperty("Value").GetString());
                Assert.Equal(id, document.RootElement.GetProperty("InstanceId").GetProperty("Value").GetString());
                Assert.Equal(new string('a', 64), document.RootElement.GetProperty("IdentityDigest").GetProperty("Value").GetString());
                string image = Path.Combine(target, "NexusPipeline.exe");
                File.WriteAllText(image, "fresh image");
                var inventory = new[] { new InstalledPayloadFile("NexusPipeline.exe", UpdateApply.ImageHash(image)) };
                string manifest = Path.Combine(root, "manifest.json");
                File.WriteAllText(manifest, JsonSerializer.Serialize(inventory));
                InstallationOwnership.RegisterOwnedInventoryForTest(target, "0.17.1", manifest, inventory);
                Assert.Equal("active", InstallationOwnership.Read(target)!.State);
                Assert.Equal(archived, System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(File.ReadAllText(recovery)), null, System.Security.Cryptography.DataProtectionScope.CurrentUser)));
            }
            finally
            {
                Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE", savedId);
                Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT", savedRoot);
                Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
                Registry.CurrentUser.DeleteSubKeyTree(uninstallPath, false);
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void GenerationIsSharedAndOverlappingOldRootsStayUntouched()
    {
        Assert.Equal("NexusPipeline." + InstallationGeneration.Id, ControlApiContract.ServiceName);
        var status = new JsonObject { ["service"] = ControlApiContract.ServiceName,
            ["controlApiVersion"] = ControlApiContract.Version, ["actualPort"] = 12345 };
        Assert.True(CliTransport.IsCompatibleStatus(status));
        Assert.False(CliTransport.IsDifferentGeneration(status));
        foreach (string service in new[] { "NexusPipeline", "NexusPipeline.gOther", "Unrelated" })
        {
            status["service"] = service;
            Assert.False(CliTransport.IsCompatibleStatus(status));
            Assert.Equal(service != "Unrelated", CliTransport.IsDifferentGeneration(status));
        }
        Assert.Equal(@"Software\NexusPipeline.Generations\" + InstallationGeneration.Id + @"\Installer",
            InstallationOwnership.RegistryPath);
        Assert.EndsWith("NexusPipeline.PerUser." + InstallationGeneration.Id + "_is1",
            InstallationOwnership.CurrentUninstallRegistryPath);
        string root = Path.Combine(Path.GetTempPath(), "nxp-generation-" + Guid.NewGuid().ToString("N"));
        string old = Path.Combine(root, "old", "app");
        Directory.CreateDirectory(old);
        string evidence = Path.Combine(old, "identity.protected");
        byte[] bytes = [0, 255, 13, 10, 42];
        File.WriteAllBytes(evidence, bytes);
        try
        {
            foreach (string target in new[] { old, old.ToUpperInvariant(), Path.Combine(old, "new"), Path.GetDirectoryName(old)! })
                Assert.Throws<IOException>(() => InstallationOwnership.RequireSeparateRoots(target, [old]));
            InstallationOwnership.RequireSeparateRoots(Path.Combine(root, "new", "app"), [old]);
            InstallationOwnership.RequireSeparateRoots(Path.Combine(root, "old", "app-other"), [old]);
            Assert.Equal(bytes, File.ReadAllBytes(evidence));
            Assert.Single(Directory.EnumerateFiles(old));
        }
        finally { Directory.Delete(root, true); }
    }
}
