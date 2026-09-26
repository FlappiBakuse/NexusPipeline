using Microsoft.Win32;
using System.Text.Json;
using NexusPipeline.Modules.Updates;
using Xunit;

namespace NexusPipeline.Tests.Updates;

public sealed class InstallationOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentPayloadIsRemovedAndRetainedIdentityAllowsExplicitReinstall(bool deleteData)
    {
        WithOwnedScope(root =>
        {
            string install = Path.Combine(root, "instance");
            Directory.CreateDirectory(Path.Combine(install, "wwwroot"));
            Directory.CreateDirectory(Path.Combine(install, "config"));
            File.WriteAllText(Path.Combine(install, "nexus-pipeline.exe"), "version N");
            File.WriteAllText(Path.Combine(install, "wwwroot", "index.html"), "version N");
            File.WriteAllText(Path.Combine(install, "README.md"), "readme");
            File.WriteAllText(Path.Combine(install, "config", "account-owned.json"), "{}");
            string unknown = Path.Combine(install, "wwwroot", "user-created.txt");
            File.WriteAllText(unknown, "preserve unknown");
            string manifest = Path.Combine(root, "manifest.json");
            string[] payload = ["nexus-pipeline.exe", "wwwroot/index.html", "README.md"];
            File.WriteAllText(manifest, JsonSerializer.Serialize(payload.Select(path =>
                new InstalledPayloadFile(path, UpdateApply.ImageHash(Path.Combine(install, path))))));
            InstallationOwnership.Register(install, "0.16.8", manifest);
            string id = InstallationOwnership.Read(install)!.InstanceId;
            string backup = Path.Combine(root, "backup"); Directory.CreateDirectory(backup);
            InstallationOwnership.SnapshotForUpdate(install, backup);
            File.WriteAllText(Path.Combine(install, "nexus-pipeline.exe"), "version N+1");
            File.WriteAllText(Path.Combine(install, "wwwroot", "index.html"), "version N+1");
            // Update replaces wwwroot from its validated package. Preserve user files outside that replacement.
            File.Move(unknown, Path.Combine(install, "user-created.txt"));
            InstallationOwnership.RefreshAfterUpdate(install, "0.16.9");
            Assert.Equal("0.16.9", InstallationOwnership.Read(install)!.Version);
            InstallationOwnership.Uninstall(install, deleteData);
            Assert.False(File.Exists(Path.Combine(install, "nexus-pipeline.exe")));
            Assert.False(File.Exists(Path.Combine(install, "wwwroot", "index.html")));
            Assert.Equal("preserve unknown", File.ReadAllText(Path.Combine(install, "user-created.txt")));
            Assert.Equal(!deleteData, File.Exists(Path.Combine(install, "config", "account-owned.json")));
            Assert.Equal("retained-data", InstallationOwnership.Read(install)!.State);
            Assert.Equal(id, InstallationOwnership.Read(install)!.InstanceId);
            File.WriteAllText(Path.Combine(install, "nexus-pipeline.exe"), "version N+1");
            File.WriteAllText(Path.Combine(install, "wwwroot", "index.html"), "version N+1");
            File.WriteAllText(Path.Combine(install, "README.md"), "readme");
            File.WriteAllText(manifest, JsonSerializer.Serialize(payload.Select(path =>
                new InstalledPayloadFile(path, UpdateApply.ImageHash(Path.Combine(install, path))))));
            InstallationOwnership.Register(install, "0.16.9", manifest);
            Assert.Equal(id, InstallationOwnership.Read(install)!.InstanceId);
            Assert.Equal("active", InstallationOwnership.Read(install)!.State);
        });
    }

    [Fact]
    public void TamperedIdentityAndUnfinishedRecoveryCannotAuthorizeDeletion()
    {
        WithOwnedScope(root =>
        {
            string install = Path.Combine(root, "instance"); Directory.CreateDirectory(install);
            string exe = Path.Combine(install, "nexus-pipeline.exe"); File.WriteAllText(exe, "owned application");
            string manifest = Path.Combine(root, "manifest.json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new[] { new InstalledPayloadFile("nexus-pipeline.exe", UpdateApply.ImageHash(exe)) }));
            InstallationOwnership.Register(install, "0.16.9", manifest);
            string user = Path.Combine(install, "data", "script", "user"); Directory.CreateDirectory(user);
            string journal = Path.Combine(user, ".session"); File.WriteAllText(journal, "unreadable preserved recovery");
            Assert.Throws<IOException>(() => InstallationOwnership.Uninstall(install, true));
            Assert.Equal("owned application", File.ReadAllText(exe));
            Assert.Equal("unreadable preserved recovery", File.ReadAllText(journal));
            string identity = Path.Combine(InstallationOwnership.ManagerDirectory, "identity.protected");
            File.AppendAllText(identity, "tampered");
            Assert.Throws<IOException>(() => InstallationOwnership.Uninstall(install, false));
            Assert.True(File.Exists(exe));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PayloadJunctionBlocksAllDeletionBeforeAnyOwnedFileIsRemoved(bool deleteData)
    {
        WithOwnedScope(root =>
        {
            string install = Path.Combine(root, "instance");
            string web = Path.Combine(install, "wwwroot");
            string held = Path.Combine(root, "held-wwwroot");
            string external = Path.Combine(root, "external");
            Directory.CreateDirectory(web);
            Directory.CreateDirectory(external);
            string exe = Path.Combine(install, "nexus-pipeline.exe");
            string readme = Path.Combine(install, "README.md");
            File.WriteAllText(exe, "owned application");
            File.WriteAllText(readme, "owned readme");
            File.WriteAllText(Path.Combine(web, "index.html"), "owned web");
            File.WriteAllText(Path.Combine(external, "index.html"), "external game");
            string manifest = Path.Combine(root, "manifest.json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new[] { "nexus-pipeline.exe", "README.md", "wwwroot/index.html" }
                .Select(path => new InstalledPayloadFile(path, UpdateApply.ImageHash(Path.Combine(install, path))))));
            InstallationOwnership.Register(install, "0.16.9", manifest);
            Directory.Move(web, held);
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (string argument in new[] { "/c", "mklink", "/J", web, external }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            Assert.True(process.WaitForExit(10000));
            Assert.Equal(0, process.ExitCode);
            try
            {
                Assert.Throws<IOException>(() => InstallationOwnership.Uninstall(install, deleteData));
                Assert.Equal("owned application", File.ReadAllText(exe));
                Assert.Equal("owned readme", File.ReadAllText(readme));
                Assert.Equal("owned web", File.ReadAllText(Path.Combine(held, "index.html")));
                Assert.Equal("external game", File.ReadAllText(Path.Combine(external, "index.html")));
                Assert.Equal("active", InstallationOwnership.Read(install)!.State);
            }
            finally { Directory.Delete(web); Directory.Move(held, web); }
        });
    }

    private static void WithOwnedScope(Action<string> verify)
    {
        string id = Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "nxp-installer-" + id); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".nxp-installer-lab"), id);
        string key = @"Software\NexusPipeline\Tests\Installer\" + id;
        Assert.Null(Registry.CurrentUser.OpenSubKey(key, false));
        string? previousId = Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE");
        string? previousRoot = Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT");
        Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE", id);
        Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT", root);
        try { verify(root); }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, false);
            Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE", previousId);
            Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT", previousRoot);
            Directory.Delete(root, true);
        }
    }
}
