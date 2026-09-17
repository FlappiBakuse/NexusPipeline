using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins;
using NexusPipeline.Services.Networking;
using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class PluginInstallRecoveryTests
{
    [Fact]
    public void ApplyPending_InstallsAndUninstallsPluginTransactionally()
    {
        string root = NewTempDir();
        try
        {
            string plugins = Path.Combine(root, "plugins");
            string pending = Path.Combine(root, "state", "pending.json");
            string ownership = Path.Combine(root, "state", "ownership.json");
            string staging = Path.Combine(root, "state", "staging");
            string backup = Path.Combine(root, "state", "backup");
            string staged = Path.Combine(staging, "bettergi.1");
            Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(staged, "plugin.json"), "{}");

            PluginInstallRecovery.AddPending(new PluginPendingOperation
            {
                Action = "install",
                Name = "bettergi",
                ArtifactName = "BetterGI",
                Version = "0.1.0",
                Kind = "data-specialized",
                StagedPath = staged,
                Phase = "pending",
            }, pending);

            Assert.True(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup));
            Assert.True(File.Exists(Path.Combine(plugins, "BetterGI", "plugin.json")));
            Assert.Empty(PluginInstallRecovery.ReadPending(pending));
            Assert.False(Directory.Exists(staging));
            Assert.False(Directory.Exists(backup));
            PluginOwnership installed = Assert.Single(PluginInstallRecovery.ReadOwnership(ownership).Values);
            Assert.Equal("bettergi", installed.Name);
            Assert.Equal("0.1.0", installed.Version);

            PluginInstallRecovery.AddPending(new PluginPendingOperation
            {
                Action = "uninstall",
                Name = "bettergi",
                ArtifactName = "BetterGI",
                Version = "0.1.0",
                Kind = "data-specialized",
                StagedPath = Path.Combine(staging, "uninstall.bettergi"),
                Phase = "pending",
            }, pending);

            Assert.True(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup));
            Assert.False(Directory.Exists(Path.Combine(plugins, "BetterGI")));
            Assert.Empty(PluginInstallRecovery.ReadOwnership(ownership));
            Assert.Empty(PluginInstallRecovery.ReadPending(pending));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void ApplyPending_UsesArtifactNameForPhysicalDirectory()
    {
        string root = NewTempDir();
        try
        {
            string plugins = Path.Combine(root, "plugins");
            string pending = Path.Combine(root, "state", "pending.json");
            string ownership = Path.Combine(root, "state", "ownership.json");
            string staging = Path.Combine(root, "state", "staging");
            string backup = Path.Combine(root, "state", "backup");
            string staged = Path.Combine(staging, "bettergi.1");
            Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(staged, "plugin.json"), "{}");

            PluginInstallRecovery.AddPending(new PluginPendingOperation
            {
                Action = "install",
                Name = "bettergi",
                ArtifactName = "BetterGI",
                Version = "0.1.1",
                Kind = "data-specialized",
                StagedPath = staged,
                Phase = "pending",
            }, pending);

            Assert.True(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup));
            string[] directories = Directory.GetDirectories(plugins)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Cast<string>()
                .ToArray();
            Assert.Equal(new[] { "BetterGI" }, directories);
            PluginOwnership installed = Assert.Single(PluginInstallRecovery.ReadOwnership(ownership).Values);
            Assert.Equal("BetterGI", installed.ArtifactName);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void ApplyPending_CompletesSwapWhenJournalWriteWasInterrupted()
    {
        string root = NewTempDir();
        try
        {
            string plugins = Path.Combine(root, "plugins");
            string pending = Path.Combine(root, "state", "pending.json");
            string ownership = Path.Combine(root, "state", "ownership.json");
            string staging = Path.Combine(root, "state", "staging");
            string backup = Path.Combine(root, "state", "backup");
            string local = Path.Combine(plugins, "BetterGI");
            string backupPath = Path.Combine(backup, "BetterGI.previous");
            Directory.CreateDirectory(local);
            Directory.CreateDirectory(backupPath);
            File.WriteAllText(Path.Combine(local, "new.txt"), "new");
            File.WriteAllText(Path.Combine(backupPath, "old.txt"), "old");

            PluginInstallRecovery.AddPending(new PluginPendingOperation
            {
                Action = "update",
                Name = "bettergi",
                ArtifactName = "BetterGI",
                Version = "0.2.0",
                Kind = "data-specialized",
                StagedPath = Path.Combine(staging, "missing-after-swap"),
                BackupPath = backupPath,
                Phase = "backed-up",
            }, pending);

            Assert.True(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup));
            Assert.True(File.Exists(Path.Combine(local, "new.txt")));
            Assert.False(File.Exists(Path.Combine(local, "old.txt")));
            Assert.Empty(PluginInstallRecovery.ReadPending(pending));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void ApplyPending_FailedStageKeepsJournalForRetry()
    {
        string root = NewTempDir();
        try
        {
            string pending = Path.Combine(root, "state", "pending.json");
            string plugins = Path.Combine(root, "plugins");
            string staging = Path.Combine(root, "state", "staging");
            string backup = Path.Combine(root, "state", "backup");
            string ownership = Path.Combine(root, "state", "ownership.json");
            Directory.CreateDirectory(Path.Combine(plugins, "BetterGI"));
            File.WriteAllText(Path.Combine(plugins, "BetterGI", "old.txt"), "old");

            PluginInstallRecovery.AddPending(new PluginPendingOperation
            {
                Action = "update",
                Name = "bettergi",
                ArtifactName = "BetterGI",
                Version = "0.2.0",
                Kind = "data-specialized",
                StagedPath = Path.Combine(staging, "missing"),
                Phase = "pending",
            }, pending);

            Assert.False(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup));
            Assert.Equal("old", File.ReadAllText(Path.Combine(plugins, "BetterGI", "old.txt")));
            Assert.Single(PluginInstallRecovery.ReadPending(pending));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    private static string NewTempDir()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-plugin-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempDir(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
