using System.Text.Json;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Services.Configuration;
using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class ConfigStoreTransactionTests
{
    [Fact]
    public void Apply_UsesFileDeltaForLargeMostlyUnchangedStore()
    {
        string scriptId = "txn-delta-" + Guid.NewGuid().ToString("N");
        string userKey = "user-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "np-txn-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(root, "config");
        Directory.CreateDirectory(config);
        try
        {
            for (int index = 0; index < 10_000; index++)
            {
                File.WriteAllText(Path.Combine(config, $"f-{index:D5}.json"), $"{{\"value\":{index}}}");
            }

            ConfigStoreTransactionResult initial = ConfigStoreTransaction.Apply(
                scriptId,
                userKey,
                config,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                null,
                null,
                Mark(scriptId, userKey, config));
            Assert.Equal(10_000, initial.Added);
            Assert.Equal(0, initial.Changed);
            Assert.Equal(0, initial.Deleted);

            File.WriteAllText(Path.Combine(config, "f-00001.json"), "{\"value\":\"changed-1\"}");
            File.WriteAllText(Path.Combine(config, "f-00002.json"), "{\"value\":\"changed-2\"}");
            File.WriteAllText(Path.Combine(config, "f-00003.json"), "{\"value\":\"changed-3\"}");
            File.WriteAllText(Path.Combine(config, "added-a.json"), "{\"value\":\"a\"}");
            File.WriteAllText(Path.Combine(config, "added-b.json"), "{\"value\":\"b\"}");
            File.Delete(Path.Combine(config, "f-00004.json"));

            ConfigStoreTransactionResult delta = ConfigStoreTransaction.Apply(
                scriptId,
                userKey,
                config,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                null,
                null,
                Mark(scriptId, userKey, config));

            Assert.Equal(2, delta.Added);
            Assert.Equal(3, delta.Changed);
            Assert.Equal(1, delta.Deleted);
            string store = ConfigSwapPaths.StoreDir(scriptId, userKey);
            Assert.Equal("{\"value\":\"changed-3\"}", File.ReadAllText(Path.Combine(store, "f-00003.json")));
            Assert.False(File.Exists(Path.Combine(store, "f-00004.json")));
            Assert.False(Directory.Exists(ConfigSwapPaths.StoreTransactionDir(scriptId, userKey)));
            Assert.False(Directory.Exists(ConfigSwapPaths.StorePreviousDir(scriptId, userKey)));
            Assert.False(Directory.Exists(ConfigSwapPaths.StoreTempDir(scriptId, userKey)));
            Assert.False(Directory.Exists(ConfigSwapPaths.RetryStoreDir(scriptId, userKey)));
            Assert.Equal(2, ConfigStoreMetadata.Load(scriptId, userKey)!.Generation);
        }
        finally
        {
            DeleteScriptData(scriptId);
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void Recovery_RollsBackUncommittedDelta()
    {
        string scriptId = "txn-rollback-" + Guid.NewGuid().ToString("N");
        string userKey = "user-" + Guid.NewGuid().ToString("N");
        string config = Path.Combine(Path.GetTempPath(), "np-txn-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(config);
        string store = ConfigSwapPaths.StoreDir(scriptId, userKey);
        string transaction = ConfigSwapPaths.StoreTransactionDir(scriptId, userKey);
        try
        {
            Directory.CreateDirectory(store);
            File.WriteAllText(Path.Combine(store, "state.json"), "old");
            Directory.CreateDirectory(ConfigSwapPaths.StoreTransactionStageDir(scriptId, userKey));
            File.WriteAllText(Path.Combine(ConfigSwapPaths.StoreTransactionStageDir(scriptId, userKey), "state.json"), "new");
            Directory.CreateDirectory(ConfigSwapPaths.StoreTransactionRollbackDir(scriptId, userKey));
            File.WriteAllText(Path.Combine(ConfigSwapPaths.StoreTransactionRollbackDir(scriptId, userKey), "state.json"), "old");
            File.WriteAllText(Path.Combine(store, "state.json"), "new");

            WriteManifest(scriptId, userKey, new ConfigStoreTransactionManifest
            {
                TransactionId = "uncommitted",
                ScriptId = scriptId,
                UserKey = userKey,
                Operations =
                {
                    new ConfigStoreTransactionOperation { Action = "replace", RelativePath = "state.json", HadPrevious = true },
                },
                NextMetadata = ConfigStoreMetadata.For(config),
            });

            ConfigStoreTransactionRecovery.Recover(scriptId, userKey);

            Assert.Equal("old", File.ReadAllText(Path.Combine(store, "state.json")));
            Assert.False(Directory.Exists(transaction));
        }
        finally
        {
            DeleteScriptData(scriptId);
            DeleteDirectory(config);
        }
    }

    [Fact]
    public void Recovery_CommittedDeltaFinishesMetadataAndCleanup()
    {
        string scriptId = "txn-commit-" + Guid.NewGuid().ToString("N");
        string userKey = "user-" + Guid.NewGuid().ToString("N");
        string config = Path.Combine(Path.GetTempPath(), "np-txn-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(config);
        try
        {
            string store = ConfigSwapPaths.StoreDir(scriptId, userKey);
            Directory.CreateDirectory(store);
            File.WriteAllText(Path.Combine(store, "state.json"), "new");
            ConfigStoreMetadata previous = ConfigStoreMetadata.For(config);
            previous.Generation = 1;
            ConfigStoreMetadata.Save(scriptId, userKey, previous);
            ConfigStoreMetadata next = ConfigStoreMetadata.For(config);
            next.Generation = 2;
            next.LastCommittedTransactionId = "committed";
            Directory.CreateDirectory(ConfigSwapPaths.StoreTransactionStageDir(scriptId, userKey));
            WriteManifest(scriptId, userKey, new ConfigStoreTransactionManifest
            {
                TransactionId = "committed",
                ScriptId = scriptId,
                UserKey = userKey,
                Operations =
                {
                    new ConfigStoreTransactionOperation { Action = "replace", RelativePath = "state.json", HadPrevious = true },
                },
                PreviousMetadata = previous,
                NextMetadata = next,
            });
            JsonUtil.WriteAtomic(
                ConfigSwapPaths.StoreTransactionCommitPath(scriptId, userKey),
                JsonSerializer.Serialize(new ConfigStoreTransactionCommit
                {
                    TransactionId = "committed",
                    Generation = 2,
                }));

            ConfigStoreTransactionRecovery.Recover(scriptId, userKey);

            Assert.Equal("committed", ConfigStoreMetadata.Load(scriptId, userKey)!.LastCommittedTransactionId);
            Assert.False(Directory.Exists(ConfigSwapPaths.StoreTransactionDir(scriptId, userKey)));
        }
        finally
        {
            DeleteScriptData(scriptId);
            DeleteDirectory(config);
        }
    }

    [Fact]
    public void Recovery_CorruptManifestIsQuarantinedAndBlocksWrites()
    {
        string scriptId = "txn-corrupt-" + Guid.NewGuid().ToString("N");
        string userKey = "user-" + Guid.NewGuid().ToString("N");
        string store = ConfigSwapPaths.StoreDir(scriptId, userKey);
        try
        {
            Directory.CreateDirectory(store);
            File.WriteAllText(Path.Combine(store, "state.json"), "safe");
            Directory.CreateDirectory(ConfigSwapPaths.StoreTransactionDir(scriptId, userKey));
            File.WriteAllText(ConfigSwapPaths.StoreTransactionManifestPath(scriptId, userKey), "{broken");

            Assert.Throws<IOException>(() => ConfigStoreTransactionRecovery.Recover(scriptId, userKey));

            Assert.Equal("safe", File.ReadAllText(Path.Combine(store, "state.json")));
            Assert.True(File.Exists(ConfigSwapPaths.StoreTransactionBlockedPath(scriptId, userKey)));
            Assert.NotEmpty(Directory.GetDirectories(
                Path.GetDirectoryName(ConfigSwapPaths.StoreTransactionDir(scriptId, userKey))!,
                "store-txn.corrupt-*"));
        }
        finally
        {
            DeleteScriptData(scriptId);
        }
    }

    [Fact]
    public void Apply_RejectsFileDirectoryShapeConflictBeforeMutation()
    {
        string scriptId = "txn-shape-" + Guid.NewGuid().ToString("N");
        string userKey = "user-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "np-txn-shape-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(root, "config");
        string store = ConfigSwapPaths.StoreDir(scriptId, userKey);
        Directory.CreateDirectory(Path.Combine(config, "a"));
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(config, "a", "nested.json"), "new");
        File.WriteAllText(Path.Combine(store, "a"), "old");
        try
        {
            Assert.Throws<IOException>(() => ConfigStoreTransaction.Apply(
                scriptId,
                userKey,
                config,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                null,
                null,
                Mark(scriptId, userKey, config)));

            Assert.Equal("old", File.ReadAllText(Path.Combine(store, "a")));
            Assert.False(File.Exists(Path.Combine(store, "a", "nested.json")));
        }
        finally
        {
            DeleteScriptData(scriptId);
            DeleteDirectory(root);
        }
    }

    private static ConfigSessionMark Mark(string scriptId, string userKey, string config) => new()
    {
        ScriptId = scriptId,
        UserName = userKey,
        UserId = userKey,
        ConfigPath = config,
        OriginalKind = "dir",
        ConfigKind = "dir",
        Phase = "run",
        SessionPhase = "run",
    };

    private static void WriteManifest(string scriptId, string userKey, ConfigStoreTransactionManifest manifest)
    {
        JsonUtil.WriteAtomic(
            ConfigSwapPaths.StoreTransactionManifestPath(scriptId, userKey),
            JsonSerializer.Serialize(manifest, JsonOpts.Indented));
    }

    private static void DeleteScriptData(string scriptId)
    {
        DeleteDirectory(Path.Combine(AppPaths.DataDir, scriptId));
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
