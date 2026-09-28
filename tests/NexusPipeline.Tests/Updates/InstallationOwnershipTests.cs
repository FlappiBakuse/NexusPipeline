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

    [Theory]
    [InlineData("not-launched")]
    [InlineData("rolled-back")]
    [InlineData("committed-cleanup-failed")]
    [InlineData("committed")]
    public void InnoMetadataFollowsProductTransaction(string outcome)
    {
        WithOwnedScope(root =>
        {
            string install = Path.Combine(root, "instance");
            Directory.CreateDirectory(install);
            string exe = Path.Combine(install, "nexus-pipeline.exe");
            File.WriteAllText(exe, "A image");
            string manifest = Path.Combine(root, "manifest.json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new[]
            {
                new InstalledPayloadFile("nexus-pipeline.exe", UpdateApply.ImageHash(exe)),
            }));
            InstallationOwnership.Register(install, "0.16.9001", manifest);
            string manager = InstallationOwnership.ManagerDirectory;
            string uninstaller = Path.Combine(manager, "unins000.exe");
            string uninstallData = Path.Combine(manager, "unins000.dat");
            File.WriteAllText(uninstaller, "A uninstaller");
            File.WriteAllText(uninstallData, "A data");
            string arp = InstallationOwnership.CurrentUninstallRegistryPath;
            using (var key = Registry.CurrentUser.CreateSubKey(arp, true)!)
            {
                key.SetValue("DisplayVersion", "0.16.9001", RegistryValueKind.String);
                key.SetValue("InstallLocation", install, RegistryValueKind.String);
                key.SetValue("EstimatedSize", 12, RegistryValueKind.DWord);
            }
            string transaction = Guid.NewGuid().ToString("N");
            string targetHash = UpdateApply.ImageHash(exe);
            File.WriteAllText(exe, "B image");
            targetHash = UpdateApply.ImageHash(exe);
            File.WriteAllText(exe, "A image");
            InstallerMetadataCheckpoint.Begin(install, transaction, "0.16.9002", targetHash, true);
            using (var key = Registry.CurrentUser.CreateSubKey(arp, true)!)
            {
                key.SetValue("DisplayVersion", "0.16.9002", RegistryValueKind.String);
                key.SetValue("EstimatedSize", 42, RegistryValueKind.DWord);
            }
            File.WriteAllText(uninstaller, "B uninstaller");
            File.WriteAllText(uninstallData, "B data");
            InstallerMetadataCheckpoint.Observe(install, transaction);

            if (outcome is "committed" or "committed-cleanup-failed")
            {
                File.WriteAllText(exe, "B image");
                InstallationOwnership.RefreshAfterUpdate(install, "0.16.9002");
                WriteTransaction(install, transaction, "0.16.9002", outcome == "committed",
                    outcome == "committed" ? "committed" : "committed_cleanup_pending");
                if (outcome == "committed-cleanup-failed")
                    File.WriteAllText(Path.Combine(install, ".nxp-version"), "0.16.9002");
            }
            else if (outcome == "rolled-back")
            {
                string taskPath = Path.Combine(install, ".nxp-update", "task.json");
                new UpdateTask("apply", "0.16.9002", Path.Combine(install, "stage"), UpdatePhase.RollbackConfirmed)
                { TransactionId = transaction, TargetImageHash = targetHash }.Write(taskPath);
                WriteTransaction(install, transaction, "0.16.9002", false, "apply_failed");
            }

            string resolved = InstallerMetadataCheckpoint.Resolve(install, transaction,
                outcome != "not-launched", outcome == "committed" ? 0 : 1, false);
            bool committed = outcome is "committed" or "committed-cleanup-failed";
            Assert.Equal(committed ? "Committed" : "Restored", resolved);
            using var finalKey = Registry.CurrentUser.OpenSubKey(arp, false)!;
            Assert.Equal(committed ? "0.16.9002" : "0.16.9001", finalKey.GetValue("DisplayVersion"));
            Assert.Equal(committed ? 42 : 12, finalKey.GetValue("EstimatedSize"));
            Assert.Equal(committed ? "B uninstaller" : "A uninstaller", File.ReadAllText(uninstaller));
            Assert.Equal(committed ? "B data" : "A data", File.ReadAllText(uninstallData));
            Assert.Equal(committed ? "0.16.9002" : "0.16.9001", InstallationOwnership.Read(install)!.Version);
        });
    }

    [Fact]
    public void FailedFirstRegistrationRemovesInnoSuccessMetadataButRetainsPayload()
    {
        WithOwnedScope(root =>
        {
            string install = Path.Combine(root, "instance");
            Directory.CreateDirectory(install);
            string transaction = Guid.NewGuid().ToString("N");
            string exe = Path.Combine(install, "nexus-pipeline.exe");
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("new image"u8.ToArray())).ToLowerInvariant();
            InstallerMetadataCheckpoint.Begin(install, transaction, "0.16.9", hash, false);
            File.WriteAllText(exe, "new image");
            string arp = InstallationOwnership.CurrentUninstallRegistryPath;
            using (var key = Registry.CurrentUser.CreateSubKey(arp, true)!)
                key.SetValue("DisplayVersion", "0.16.9", RegistryValueKind.String);
            string uninstaller = Path.Combine(InstallationOwnership.ManagerDirectory, "unins000.exe");
            File.WriteAllText(uninstaller, "new uninstaller");
            InstallerMetadataCheckpoint.Observe(install, transaction);
            Assert.Equal("Restored", InstallerMetadataCheckpoint.Resolve(install, transaction, true, 1, true));
            Assert.Null(Registry.CurrentUser.OpenSubKey(arp, false));
            Assert.False(File.Exists(uninstaller));
            Assert.Equal("new image", File.ReadAllText(exe));
        });
    }

    [Fact]
    public void PartialRegistrationDoesNotLeaveSuccessfulArpEntry()
    {
        WithOwnedScope(root =>
        {
            string install = Path.Combine(root, "instance");
            Directory.CreateDirectory(install);
            string transaction = Guid.NewGuid().ToString("N");
            string exe = Path.Combine(install, "nexus-pipeline.exe");
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("new image"u8.ToArray())).ToLowerInvariant();
            InstallerMetadataCheckpoint.Begin(install, transaction, "0.16.9", hash, false);
            File.WriteAllText(exe, "new image");
            string arp = InstallationOwnership.CurrentUninstallRegistryPath;
            using (var key = Registry.CurrentUser.CreateSubKey(arp, true)!)
                key.SetValue("DisplayVersion", "0.16.9", RegistryValueKind.String);
            string uninstaller = Path.Combine(InstallationOwnership.ManagerDirectory, "unins000.exe");
            File.WriteAllText(uninstaller, "new uninstaller");
            InstallerMetadataCheckpoint.Observe(install, transaction);
            string manifest = Path.Combine(root, "manifest.json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new[]
            {
                new InstalledPayloadFile("nexus-pipeline.exe", hash),
            }));
            InstallationOwnership.Register(install, "0.16.9", manifest);
            Assert.Throws<IOException>(() =>
                InstallerMetadataCheckpoint.Resolve(install, transaction, true, 1, true));
            Assert.Null(Registry.CurrentUser.OpenSubKey(arp, false));
            Assert.False(File.Exists(uninstaller));
            Assert.True(Directory.Exists(Path.Combine(InstallationOwnership.ManagerDirectory, "metadata-checkpoint")));
        });
    }

    [Fact]
    public void ConcurrentUninstallerChangeKeepsCheckpointAndRejectsOverwrite()
    {
        WithOwnedScope(root =>
        {
            string install = Path.Combine(root, "instance");
            Directory.CreateDirectory(install);
            string transaction = Guid.NewGuid().ToString("N");
            string arp = InstallationOwnership.CurrentUninstallRegistryPath;
            string hash = new string('a', 64);
            InstallerMetadataCheckpoint.Begin(install, transaction, "0.16.9", hash, false);
            using (var key = Registry.CurrentUser.CreateSubKey(arp, true)!)
                key.SetValue("DisplayVersion", "0.16.9", RegistryValueKind.String);
            string uninstaller = Path.Combine(InstallationOwnership.ManagerDirectory, "unins000.exe");
            File.WriteAllText(uninstaller, "Inno bytes");
            InstallerMetadataCheckpoint.Observe(install, transaction);
            File.WriteAllText(uninstaller, "external changed bytes");
            Assert.Throws<IOException>(() =>
                InstallerMetadataCheckpoint.Resolve(install, transaction, false, 1223, false));
            Assert.Equal("external changed bytes", File.ReadAllText(uninstaller));
            using (var key = Registry.CurrentUser.OpenSubKey(arp, false)!)
                Assert.Equal("0.16.9", key.GetValue("DisplayVersion"));
            Assert.True(Directory.Exists(Path.Combine(InstallationOwnership.ManagerDirectory, "metadata-checkpoint")));
        });
    }

    [Fact]
    public void NextInstallerReconcilesObservedUnlaunchedCheckpoint()
    {
        WithOwnedScope(root =>
        {
            string install = Path.Combine(root, "instance");
            Directory.CreateDirectory(install);
            string transaction = Guid.NewGuid().ToString("N");
            InstallerMetadataCheckpoint.Begin(install, transaction, "0.16.9", new string('a', 64), false);
            string arp = InstallationOwnership.CurrentUninstallRegistryPath;
            using (var key = Registry.CurrentUser.CreateSubKey(arp, true)!)
                key.SetValue("DisplayVersion", "0.16.9", RegistryValueKind.String);
            InstallerMetadataCheckpoint.Observe(install, transaction);
            Assert.Equal("Restored", InstallerMetadataCheckpoint.RecoverPending(install));
            Assert.Null(Registry.CurrentUser.OpenSubKey(arp, false));
            Assert.Equal("None", InstallerMetadataCheckpoint.RecoverPending(install));
        });
    }

    [Fact]
    public void NextInstallerArchivesPreparedCheckpointWhenInnoWroteNothing()
    {
        WithOwnedScope(root =>
        {
            string install = Path.Combine(root, "instance");
            Directory.CreateDirectory(install);
            string transaction = Guid.NewGuid().ToString("N");
            InstallerMetadataCheckpoint.Begin(install, transaction, "0.16.9", new string('a', 64), false);
            Assert.Equal("Aborted", InstallerMetadataCheckpoint.RecoverPending(install));
            Assert.False(Directory.Exists(Path.Combine(InstallationOwnership.ManagerDirectory, "metadata-checkpoint")));
            Assert.Equal("None", InstallerMetadataCheckpoint.RecoverPending(install));
        });
    }

    [Fact]
    public void NextInstallerObservesInnoWriteAndRestoresFailedRegistration()
    {
        WithOwnedScope(root =>
        {
            string install = Path.Combine(root, "instance");
            Directory.CreateDirectory(install);
            string transaction = Guid.NewGuid().ToString("N");
            InstallerMetadataCheckpoint.Begin(install, transaction, "0.16.9", new string('a', 64), false);
            string arp = InstallationOwnership.CurrentUninstallRegistryPath;
            using (var key = Registry.CurrentUser.CreateSubKey(arp, true)!)
                key.SetValue("DisplayVersion", "0.16.9", RegistryValueKind.String);
            string uninstaller = Path.Combine(InstallationOwnership.ManagerDirectory, "unins000.exe");
            File.WriteAllText(uninstaller, "new uninstaller");
            Assert.Equal("Restored", InstallerMetadataCheckpoint.RecoverPending(install));
            Assert.Null(Registry.CurrentUser.OpenSubKey(arp, false));
            Assert.False(File.Exists(uninstaller));
        });
    }

    [Fact]
    public void NextInstallerRestoresPartialInnoRegistrationWithoutDisplayVersion()
    {
        WithOwnedScope(root =>
        {
            string install = Path.Combine(root, "instance");
            Directory.CreateDirectory(install);
            string transaction = Guid.NewGuid().ToString("N");
            InstallerMetadataCheckpoint.Begin(install, transaction, "0.16.9", new string('a', 64), false);
            string arp = InstallationOwnership.CurrentUninstallRegistryPath;
            using (var key = Registry.CurrentUser.CreateSubKey(arp, true)!)
                key.SetValue("DisplayName", "NexusPipeline", RegistryValueKind.String);
            string uninstaller = Path.Combine(InstallationOwnership.ManagerDirectory, "unins000.exe");
            File.WriteAllText(uninstaller, "partial uninstaller");
            Assert.Equal("Restored", InstallerMetadataCheckpoint.RecoverPending(install));
            Assert.Null(Registry.CurrentUser.OpenSubKey(arp, false));
            Assert.False(File.Exists(uninstaller));
        });
    }

    private static void WriteTransaction(string install, string transaction, string version, bool succeeded, string code)
    {
        string path = Path.Combine(install, ".nxp", "state", "updates", transaction + ".result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { TransactionId = transaction, Version = version,
            Succeeded = succeeded, Code = code }));
    }

    private static void WithOwnedScope(Action<string> verify)
    {
        string id = Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "nxp-installer-" + id); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".nxp-installer-lab"), id);
        string key = @"Software\NexusPipeline\Tests\Installer\" + id;
        string uninstallKey = @"Software\NexusPipeline\Tests\Uninstall\" + id;
        Assert.Null(Registry.CurrentUser.OpenSubKey(key, false));
        Assert.Null(Registry.CurrentUser.OpenSubKey(uninstallKey, false));
        string? previousId = Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE");
        string? previousRoot = Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT");
        Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE", id);
        Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT", root);
        try { verify(root); }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, false);
            Registry.CurrentUser.DeleteSubKeyTree(uninstallKey, false);
            Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE", previousId);
            Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT", previousRoot);
            Directory.Delete(root, true);
        }
    }
}
