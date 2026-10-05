using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Plugin.Abstractions;
using Xunit;
using NexusPipeline.Modules.Plugins.Repository;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Tests.Plugins;

public sealed class PluginInstallRecoveryTests
{
    [Fact]
    public void ApplyPending_ManagedInstallKeepsActivationIntentUntilPreferenceCommit()
    {
        string root = NewTempDir();
        try
        {
            string plugins = Path.Combine(root, "plugins");
            string pending = Path.Combine(root, "state", "pending.json");
            string ownership = Path.Combine(root, "state", "ownership.json");
            string staging = Path.Combine(root, "state", "staging");
            string backup = Path.Combine(root, "state", "backup");
            string staged = Path.Combine(staging, "bettergi.install");
            Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(staged, "plugin.json"), "{}");
            string id = Guid.NewGuid().ToString("N");
            PluginInstallRecovery.AddPending(new PluginPendingOperation
            {
                Action = "install",
                Name = "bettergi",
                ArtifactName = "BetterGI",
                Version = "0.1.0",
                Kind = "managed-code",
                Sha256 = new string('A', 64),
                StagedPath = staged,
                OperationId = id,
                EnableAfterInstall = true,
            }, pending);

            var preferences = new TestActivationPreferences { Fail = true };
            Assert.False(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup, preferences));
            Assert.True(File.Exists(Path.Combine(plugins, "BetterGI", "plugin.json")));
            Assert.Single(PluginInstallRecovery.ReadPending(pending));
            Assert.Single(PluginInstallRecovery.ReadOwnership(ownership));

            preferences.Fail = false;
            Assert.True(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup, preferences));
            Assert.Empty(PluginInstallRecovery.ReadPending(pending));
            Assert.Equal(2, preferences.Attempts);
            Assert.Equal(id, preferences.LastCompletion?.OperationId);
            Assert.Equal("BetterGI", preferences.LastCompletion?.InstalledIdentity.ArtifactName);
            Assert.True(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup, preferences));
            Assert.Equal(2, preferences.Attempts);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void ApplyPending_CurrentInstallWithoutActivationIntent()
    {
        string root = NewTempDir();
        try
        {
            string plugins = Path.Combine(root, "plugins");
            string pending = Path.Combine(root, "state", "pending.json");
            string ownership = Path.Combine(root, "state", "ownership.json");
            string staging = Path.Combine(root, "state", "staging");
            string backup = Path.Combine(root, "state", "backup");
            string staged = Path.Combine(staging, "bettergi.install");
            Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(staged, "plugin.json"), "{}");
            PluginInstallRecovery.AddPending(new PluginPendingOperation
            {
                Action = "install",
                Name = "bettergi",
                ArtifactName = "BetterGI",
                Version = "0.1.0",
                Kind = "managed-code",
                StagedPath = staged,
            }, pending);
            var preferences = new TestActivationPreferences();
            Assert.True(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup, preferences));
            Assert.Equal(0, preferences.Attempts);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    private sealed class TestActivationPreferences : IPluginActivationPreferences
    {
        public bool Fail { get; set; }
        public int Attempts { get; private set; }
        public PluginInstallCompletion? LastCompletion { get; private set; }

        public PluginActivationResult ApplyInstallIntent(PluginInstallCompletion completion)
        {
            Attempts++;
            LastCompletion = completion;
            return Fail ? PluginActivationResult.Failed : PluginActivationResult.Enabled;
        }
    }

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
            WriteOwnership(ownership, new PluginOwnership
            {
                Name = "bettergi",
                ArtifactName = "BetterGI",
                Version = "0.1.0",
                Kind = "data-specialized",
            });

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
            WriteOwnership(ownership, new PluginOwnership
            {
                Name = "bettergi",
                ArtifactName = "BetterGI",
                Version = "0.1.0",
                Kind = "data-specialized",
            });

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

    [Fact]
    public void ApplyPending_CorruptJournalIsRetainedAndDoesNotCreateRecoverySideEffects()
    {
        string root = NewTempDir();
        try
        {
            string plugins = Path.Combine(root, "plugins");
            string pending = Path.Combine(root, "state", "pending.json");
            string ownership = Path.Combine(root, "state", "ownership.json");
            string staging = Path.Combine(root, "state", "staging");
            string backup = Path.Combine(root, "state", "backup");
            Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
            const string corrupt = "{\"SchemaVersion\":2}";
            File.WriteAllText(pending, corrupt);

            Assert.False(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup));

            Assert.Equal(corrupt, File.ReadAllText(pending));
            Assert.False(Directory.Exists(plugins));
            Assert.False(Directory.Exists(staging));
            Assert.False(Directory.Exists(backup));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void ApplyPending_CorruptOwnershipIsRetainedAndDoesNotCreateRecoveryDirectories()
    {
        string root = NewTempDir();
        try
        {
            string plugins = Path.Combine(root, "plugins");
            string pending = Path.Combine(root, "state", "pending.json");
            string ownership = Path.Combine(root, "state", "ownership.json");
            string staging = Path.Combine(root, "state", "staging");
            string backup = Path.Combine(root, "state", "backup");
            string staged = Path.Combine(staging, "bettergi.install");
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
            File.WriteAllText(ownership, "{\"SchemaVersion\":2,\"Plugins\":[{\"Name\":\"bettergi\"}]}");

            Assert.False(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup));

            Assert.Single(PluginInstallRecovery.ReadPending(pending));
            Assert.Equal("{\"SchemaVersion\":2,\"Plugins\":[{\"Name\":\"bettergi\"}]}", File.ReadAllText(ownership));
            Assert.False(Directory.Exists(plugins));
            Assert.False(Directory.Exists(staging));
            Assert.False(Directory.Exists(backup));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void ReadOwnership_RejectsOldRecordAndPreservesBytes()
    {
        string root = NewTempDir();
        try
        {
            string ownership = Path.Combine(root, "state", "ownership.json");
            Directory.CreateDirectory(Path.GetDirectoryName(ownership)!);
            File.WriteAllText(
                ownership,
                "{\"SchemaVersion\":2,\"Plugins\":[{\"Name\":\"bettergi\",\"ArtifactName\":\"BetterGI\",\"Version\":\"0.1.0\",\"Kind\":\"data-specialized\",\"ApiVersion\":\"1.0\",\"Sha256\":\"\",\"InstalledAt\":\"2026-09-19T00:00:00+00:00\"}]}");

            byte[] original = File.ReadAllBytes(ownership);
            Assert.Empty(PluginInstallRecovery.ReadOwnership(ownership));
            Assert.Equal(original, File.ReadAllBytes(ownership));
            var upgraded = JsonNode.Parse(original)!.AsObject();
            upgraded["SchemaVersion"] = PluginInstallRecovery.CurrentStateSchema;
            File.WriteAllText(ownership, upgraded.ToJsonString());
            byte[] missingProvenance = File.ReadAllBytes(ownership);
            Assert.Empty(PluginInstallRecovery.ReadOwnership(ownership));
            Assert.Equal(missingProvenance, File.ReadAllBytes(ownership));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void ReadPending_RejectsOldOperationAndPreservesAllFiles()
    {
        string root = NewTempDir();
        try
        {
            string pending = Path.Combine(root, "state", "pending.json");
            string stagedPath = Path.Combine(root, "state", "staging", "bettergi.install");
            Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
            File.WriteAllText(
                pending,
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = 2,
                    Operations = new[]
                    {
                        new
                        {
                            Action = "install",
                            Name = "bettergi",
                            ArtifactName = "BetterGI",
                            Version = "0.1.0",
                            Kind = "data-specialized",
                            ApiVersion = "1.0",
                            Sha256 = "",
                            StagedPath = stagedPath,
                            BackupPath = "",
                            Phase = "pending",
                            CreatedAt = new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero),
                        },
                    },
                }));

            string plugins = Path.Combine(root, "plugins"), staging = Path.GetDirectoryName(stagedPath)!;
            string backup = Path.Combine(root, "state", "backup"), ownership = Path.Combine(root, "state", "ownership.json");
            Directory.CreateDirectory(stagedPath);
            File.WriteAllBytes(Path.Combine(stagedPath, "witness.txt"), "preserve-stage"u8.ToArray());
            foreach (int schema in new[] { 2, PluginInstallRecovery.CurrentStateSchema })
            {
                var document = JsonNode.Parse(File.ReadAllBytes(pending))!.AsObject();
                document["SchemaVersion"] = schema;
                File.WriteAllText(pending, document.ToJsonString());
                byte[] original = File.ReadAllBytes(pending);
                Assert.Throws<InvalidDataException>(() => PluginInstallRecovery.ReadPending(pending));
                Assert.False(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup));
                Assert.Equal(original, File.ReadAllBytes(pending));
                Assert.Equal("preserve-stage"u8.ToArray(), File.ReadAllBytes(Path.Combine(stagedPath, "witness.txt")));
                Assert.False(Directory.Exists(plugins));
                Assert.False(Directory.Exists(backup));
                Assert.False(File.Exists(ownership));
            }
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void ApplyPending_UnownedUpdateRetainsLocalAndStaging()
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
            string staged = Path.Combine(staging, "bettergi.update");
            Directory.CreateDirectory(local);
            File.WriteAllText(Path.Combine(local, "user-witness.txt"), "keep-local");
            Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(staged, "plugin.json"), "new");
            PluginInstallRecovery.AddPending(new PluginPendingOperation
            {
                Action = "update",
                Name = "bettergi",
                ArtifactName = "BetterGI",
                Version = "0.2.0",
                Kind = "data-specialized",
                StagedPath = staged,
                Phase = "pending",
            }, pending);

            Assert.False(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup));

            Assert.Equal("keep-local", File.ReadAllText(Path.Combine(local, "user-witness.txt")));
            Assert.True(Directory.Exists(staged));
            Assert.Single(PluginInstallRecovery.ReadPending(pending));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void ApplyPending_ThreeWayDirectoryConflictRetainsAllOwnedScenes()
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
            string staged = Path.Combine(staging, "bettergi.update");
            string backupPath = Path.Combine(backup, "BetterGI.previous");
            Directory.CreateDirectory(local);
            Directory.CreateDirectory(staged);
            Directory.CreateDirectory(backupPath);
            File.WriteAllText(Path.Combine(local, "local.txt"), "local");
            File.WriteAllText(Path.Combine(staged, "staged.txt"), "staged");
            File.WriteAllText(Path.Combine(backupPath, "backup.txt"), "backup");
            WriteOwnership(ownership, new PluginOwnership
            {
                Name = "bettergi",
                ArtifactName = "BetterGI",
                Version = "0.1.0",
                Kind = "data-specialized",
            });
            PluginInstallRecovery.AddPending(new PluginPendingOperation
            {
                Action = "update",
                Name = "bettergi",
                ArtifactName = "BetterGI",
                Version = "0.2.0",
                Kind = "data-specialized",
                StagedPath = staged,
                BackupPath = backupPath,
                Phase = "backed-up",
            }, pending);

            Assert.False(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup));

            Assert.Equal("local", File.ReadAllText(Path.Combine(local, "local.txt")));
            Assert.Equal("staged", File.ReadAllText(Path.Combine(staged, "staged.txt")));
            Assert.Equal("backup", File.ReadAllText(Path.Combine(backupPath, "backup.txt")));
            Assert.Single(PluginInstallRecovery.ReadPending(pending));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void ApplyPending_UninstallAfterOwnershipCommitFinishesWithoutDeletingUnrelatedData()
    {
        string root = NewTempDir();
        try
        {
            string plugins = Path.Combine(root, "plugins");
            string pending = Path.Combine(root, "state", "pending.json");
            string ownership = Path.Combine(root, "state", "ownership.json");
            string staging = Path.Combine(root, "state", "staging");
            string backup = Path.Combine(root, "state", "backup");
            string backupPath = Path.Combine(backup, "BetterGI.previous");
            Directory.CreateDirectory(backupPath);
            File.WriteAllText(Path.Combine(backupPath, "plugin.txt"), "old");
            File.WriteAllText(Path.Combine(root, "user-witness.txt"), "keep");
            WriteOwnership(ownership, new PluginOwnership
            {
                Name = "bettergi",
                ArtifactName = "BetterGI",
                Version = "0.1.0",
                Kind = "data-specialized",
            });
            PluginInstallRecovery.AddPending(new PluginPendingOperation
            {
                Action = "uninstall",
                Name = "bettergi",
                ArtifactName = "BetterGI",
                Version = "0.1.0",
                Kind = "data-specialized",
                StagedPath = Path.Combine(staging, "uninstall.bettergi"),
                BackupPath = backupPath,
                Phase = "swapped",
            }, pending);
            WriteOwnership(ownership);

            Assert.True(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup));

            Assert.Empty(PluginInstallRecovery.ReadPending(pending));
            Assert.Empty(PluginInstallRecovery.ReadOwnership(ownership));
            Assert.False(Directory.Exists(backupPath));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "user-witness.txt")));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void ApplyPending_LockedJournalRetainsOperationUntilNextStartupAttempt()
    {
        string root = NewTempDir();
        try
        {
            string plugins = Path.Combine(root, "plugins");
            string pending = Path.Combine(root, "state", "pending.json");
            string ownership = Path.Combine(root, "state", "ownership.json");
            string staging = Path.Combine(root, "state", "staging");
            string backup = Path.Combine(root, "state", "backup");
            string staged = Path.Combine(staging, "bettergi.install");
            Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(staged, "plugin.json"), "new");
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

            using (var held = new FileStream(pending, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.False(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup));
                Assert.Single(PluginInstallRecovery.ReadPending(pending));
                Assert.False(Directory.Exists(Path.Combine(plugins, "BetterGI")));
            }

            Assert.True(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup));
            Assert.True(File.Exists(Path.Combine(plugins, "BetterGI", "plugin.json")));
            Assert.Empty(PluginInstallRecovery.ReadPending(pending));
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

    private static void WriteOwnership(string path, params PluginOwnership[] owners)
    {
        PluginOwnershipState state = new()
        {
            SchemaVersion = PluginInstallRecovery.CurrentStateSchema,
            Plugins = owners.ToList(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonUtil.WriteAtomic(path, JsonSerializer.Serialize(state, JsonOpts.Indented));
    }
}
