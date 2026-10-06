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
            string inventory = Path.Combine(root, "manifest.json");
            File.WriteAllText(inventory, JsonSerializer.Serialize(new[] {
                new InstalledPayloadFile("NexusPipeline.exe", UpdateApply.ImageHash(image)) }));
            Assert.ThrowsAny<IOException>(() => InstallationOwnership.Register(app, "0.17.0", inventory));
            Assert.False(Directory.Exists(InstallationOwnership.ManagerDirectory));
            Assert.Equal("new image", File.ReadAllText(image));
            InstallationOwnership.RegisterOwnedInventoryForTest(app, "0.17.0", inventory,
                [new InstalledPayloadFile("NexusPipeline.exe", UpdateApply.ImageHash(image))]);
            Assert.Equal("active", InstallationOwnership.Read(app)!.State);
            Directory.CreateDirectory(Path.Combine(app, "data"));
            string data = Path.Combine(app, "data", "user.json");
            File.WriteAllText(data, "preserved data");
            InstallationOwnership.Uninstall(app, false);
            Assert.False(File.Exists(image));
            Assert.Equal("preserved data", File.ReadAllText(data));
            Assert.Equal("retained-data", InstallationOwnership.Read(app)!.State);
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
