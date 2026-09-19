using System.Text.Json;
using Xunit;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Tests.Configuration;

public sealed class ConfigStoreTransactionTests
{
    [Fact]
    public void Recovery_PartialRollbackRetainsBackupsAndCanResumeAfterFileUnlock()
    {
        string scriptId = "txn-locked-" + Guid.NewGuid().ToString("N");
        string userKey = "user-" + Guid.NewGuid().ToString("N");
        string store = ConfigPaths.StoreDir(scriptId, userKey);
        string rollback = ConfigPaths.StoreTransactionRollbackDir(scriptId, userKey);
        try
        {
            Directory.CreateDirectory(store);
            Directory.CreateDirectory(rollback);
            foreach (string name in new[] { "a.json", "b.json" })
            {
                File.WriteAllText(Path.Combine(store, name), "new-" + name);
                File.WriteAllText(Path.Combine(rollback, name), "old-" + name);
            }
            WriteManifest(scriptId, userKey, new ConfigStoreTransactionManifest
            {
                TransactionId = "partial-rollback",
                ScriptId = scriptId,
                UserKey = userKey,
                NextMetadata = ConfigStoreMetadata.For(store),
                Operations = new[] { "a.json", "b.json" }.Select(name => new ConfigStoreTransactionOperation
                {
                    Action = "replace", RelativePath = name, HadPrevious = true,
                }).ToList(),
            });
            using (var held = new FileStream(Path.Combine(store, "a.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Exception? error = Record.Exception(() => ConfigStoreTransactionRecovery.Recover(scriptId, userKey));
                Assert.True(error is IOException or UnauthorizedAccessException);
                Assert.Equal("old-b.json", File.ReadAllText(Path.Combine(store, "b.json")));
                Assert.Equal("old-b.json", File.ReadAllText(Path.Combine(rollback, "b.json")));
            }
            ConfigStoreTransactionRecovery.Recover(scriptId, userKey);
            ConfigStoreTransactionRecovery.Recover(scriptId, userKey);
            Assert.Equal("old-a.json", File.ReadAllText(Path.Combine(store, "a.json")));
            Assert.Equal("old-b.json", File.ReadAllText(Path.Combine(store, "b.json")));
            Assert.False(Directory.Exists(ConfigPaths.StoreTransactionDir(scriptId, userKey)));
        }
        finally { DeleteScriptData(scriptId); }
    }

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
            string store = ConfigPaths.StoreDir(scriptId, userKey);
            Assert.Equal("{\"value\":\"changed-3\"}", File.ReadAllText(Path.Combine(store, "f-00003.json")));
            Assert.False(File.Exists(Path.Combine(store, "f-00004.json")));
            Assert.False(Directory.Exists(ConfigPaths.StoreTransactionDir(scriptId, userKey)));
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
        string store = ConfigPaths.StoreDir(scriptId, userKey);
        string transaction = ConfigPaths.StoreTransactionDir(scriptId, userKey);
        try
        {
            Directory.CreateDirectory(store);
            File.WriteAllText(Path.Combine(store, "state.json"), "old");
            Directory.CreateDirectory(ConfigPaths.StoreTransactionStageDir(scriptId, userKey));
            File.WriteAllText(Path.Combine(ConfigPaths.StoreTransactionStageDir(scriptId, userKey), "state.json"), "new");
            Directory.CreateDirectory(ConfigPaths.StoreTransactionRollbackDir(scriptId, userKey));
            File.WriteAllText(Path.Combine(ConfigPaths.StoreTransactionRollbackDir(scriptId, userKey), "state.json"), "old");
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
            string store = ConfigPaths.StoreDir(scriptId, userKey);
            Directory.CreateDirectory(store);
            File.WriteAllText(Path.Combine(store, "state.json"), "new");
            ConfigStoreMetadata previous = ConfigStoreMetadata.For(config);
            previous.Generation = 1;
            ConfigStoreMetadata.Save(scriptId, userKey, previous);
            ConfigStoreMetadata next = ConfigStoreMetadata.For(config);
            next.Generation = 2;
            next.LastCommittedTransactionId = "committed";
            Directory.CreateDirectory(ConfigPaths.StoreTransactionStageDir(scriptId, userKey));
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
                ConfigPaths.StoreTransactionCommitPath(scriptId, userKey),
                JsonSerializer.Serialize(new ConfigStoreTransactionCommit
                {
                    TransactionId = "committed",
                    Generation = 2,
                }));

            ConfigStoreTransactionRecovery.Recover(scriptId, userKey);

            Assert.Equal("committed", ConfigStoreMetadata.Load(scriptId, userKey)!.LastCommittedTransactionId);
            Assert.False(Directory.Exists(ConfigPaths.StoreTransactionDir(scriptId, userKey)));
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
        string store = ConfigPaths.StoreDir(scriptId, userKey);
        try
        {
            Directory.CreateDirectory(store);
            File.WriteAllText(Path.Combine(store, "state.json"), "safe");
            Directory.CreateDirectory(ConfigPaths.StoreTransactionDir(scriptId, userKey));
            File.WriteAllText(ConfigPaths.StoreTransactionManifestPath(scriptId, userKey), "{broken");

            Assert.Throws<IOException>(() => ConfigStoreTransactionRecovery.Recover(scriptId, userKey));

            Assert.Equal("safe", File.ReadAllText(Path.Combine(store, "state.json")));
            Assert.True(File.Exists(ConfigPaths.StoreTransactionBlockedPath(scriptId, userKey)));
            Assert.NotEmpty(Directory.GetDirectories(
                Path.GetDirectoryName(ConfigPaths.StoreTransactionDir(scriptId, userKey))!,
                "store-txn.corrupt-*"));
        }
        finally
        {
            DeleteScriptData(scriptId);
        }
    }

    [Fact]
    public void Apply_ManifestWriteFailurePreservesStoreAndCleansUnpublishedTransaction()
    {
        string scriptId = "txn-manifest-failure-" + Guid.NewGuid().ToString("N");
        string userKey = "user-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "np-txn-manifest-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(root, "config");
        string store = ConfigPaths.StoreDir(scriptId, userKey);
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(config, "state.json"), "new");
        File.WriteAllText(Path.Combine(store, "state.json"), "old");
        try
        {
            ConfigStoreMetadata previous = ConfigStoreMetadata.For(config);
            previous.Generation = 1;
            ConfigStoreMetadata.Save(scriptId, userKey, previous);

            IOException failure = new("模拟 manifest 写入失败（access-denied）", unchecked((int)0x80070005));
            Action<string, string> writer = FailAt("manifest.json", JsonWritePhase.BeforeReplace, failure);

            IOException error = Assert.Throws<IOException>(() => ConfigStoreTransaction.Apply(
                scriptId,
                userKey,
                config,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                null,
                null,
                Mark(scriptId, userKey, config),
                writer));

            Assert.Equal(failure.HResult, error.HResult);
            Assert.Equal("old", File.ReadAllText(Path.Combine(store, "state.json")));
            Assert.Equal(1, ConfigStoreMetadata.Load(scriptId, userKey)!.Generation);
            Assert.False(Directory.Exists(ConfigPaths.StoreTransactionDir(scriptId, userKey)));
        }
        finally
        {
            DeleteScriptData(scriptId);
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void Apply_CommitWriteFailureRollsBackStoreBeforeReturningError()
    {
        string scriptId = "txn-commit-failure-" + Guid.NewGuid().ToString("N");
        string userKey = "user-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "np-txn-commit-failure-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(root, "config");
        string store = ConfigPaths.StoreDir(scriptId, userKey);
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(config, "state.json"), "new");
        File.WriteAllText(Path.Combine(store, "state.json"), "old");
        try
        {
            ConfigStoreMetadata previous = ConfigStoreMetadata.For(config);
            previous.Generation = 1;
            ConfigStoreMetadata.Save(scriptId, userKey, previous);

            Action<string, string> writer = FailAt(
                "commit.json",
                JsonWritePhase.BeforeReplace,
                new IOException("模拟提交前写入失败"));

            Assert.Throws<IOException>(() => ConfigStoreTransaction.Apply(
                scriptId,
                userKey,
                config,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                null,
                null,
                Mark(scriptId, userKey, config),
                writer));

            Assert.Equal("old", File.ReadAllText(Path.Combine(store, "state.json")));
            Assert.Equal(1, ConfigStoreMetadata.Load(scriptId, userKey)!.Generation);
            Assert.False(Directory.Exists(ConfigPaths.StoreTransactionDir(scriptId, userKey)));
        }
        finally
        {
            DeleteScriptData(scriptId);
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void Apply_MetadataWriteFailureLeavesCommittedSceneForNextRecovery()
    {
        string scriptId = "txn-metadata-failure-" + Guid.NewGuid().ToString("N");
        string userKey = "user-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "np-txn-metadata-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(root, "config");
        string store = ConfigPaths.StoreDir(scriptId, userKey);
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(config, "state.json"), "new");
        File.WriteAllText(Path.Combine(store, "state.json"), "old");
        try
        {
            ConfigStoreMetadata previous = ConfigStoreMetadata.For(config);
            previous.Generation = 1;
            ConfigStoreMetadata.Save(scriptId, userKey, previous);

            Action<string, string> writer = FailAt(
                "store-meta.json",
                JsonWritePhase.BeforeReplace,
                new IOException("模拟 metadata 写入失败"));

            Assert.Throws<IOException>(() => ConfigStoreTransaction.Apply(
                scriptId,
                userKey,
                config,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                null,
                null,
                Mark(scriptId, userKey, config),
                writer));

            Assert.Equal("new", File.ReadAllText(Path.Combine(store, "state.json")));
            Assert.True(Directory.Exists(ConfigPaths.StoreTransactionDir(scriptId, userKey)));

            ConfigStoreTransactionRecovery.Recover(scriptId, userKey);

            Assert.Equal("new", File.ReadAllText(Path.Combine(store, "state.json")));
            Assert.Equal(2, ConfigStoreMetadata.Load(scriptId, userKey)!.Generation);
            Assert.False(Directory.Exists(ConfigPaths.StoreTransactionDir(scriptId, userKey)));
            ConfigStoreTransactionRecovery.Recover(scriptId, userKey);
        }
        finally
        {
            DeleteScriptData(scriptId);
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void Apply_RejectsFileDirectoryShapeConflictBeforeMutation()
    {
        string scriptId = "txn-shape-" + Guid.NewGuid().ToString("N");
        string userKey = "user-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "np-txn-shape-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(root, "config");
        string store = ConfigPaths.StoreDir(scriptId, userKey);
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
        UserId = userKey,
        ConfigPath = config,
        ConfigKind = "dir",
        SessionPhase = "run",
    };

    private static Action<string, string> FailAt(
        string fileName,
        JsonWritePhase phase,
        Exception error)
    {
        bool failed = false;
        return (path, content) => JsonUtil.WriteAtomic(
            path,
            content,
            observed =>
            {
                if (!failed
                    && observed == phase
                    && string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    failed = true;
                    throw error;
                }
            });
    }

    private static void WriteManifest(string scriptId, string userKey, ConfigStoreTransactionManifest manifest)
    {
        JsonUtil.WriteAtomic(
            ConfigPaths.StoreTransactionManifestPath(scriptId, userKey),
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
