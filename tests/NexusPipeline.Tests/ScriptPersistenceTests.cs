using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class ScriptPersistenceTests
{
    [Fact]
    public void GenericJudgeSource_IsStoredAsSeparateAssetAndReloaded()
    {
        string root = MakeTempDir();
        try
        {
            var storage = new ScriptStorage(root);
            var script = new ScriptInstance
            {
                Id = "generic-asset",
                Name = "通用判断",
                JudgeScriptEnabled = true,
                JudgeScriptLanguage = "javascript",
                JudgeScript = "console.log('ok');",
            };

            storage.SaveScripts(new List<ScriptInstance> { script });

            JsonObject persisted = Assert.IsType<JsonObject>(Assert.Single(JsonNode.Parse(File.ReadAllText(storage.ScriptsPath))!.AsArray()));
            Assert.Null(persisted.FirstOrDefault(pair =>
                string.Equals(pair.Key, "JudgeScript", StringComparison.OrdinalIgnoreCase)).Value);
            Assert.Equal(
                script.JudgeScript,
                File.ReadAllText(storage.JudgeScripts.GetPath(script.Id, "javascript")));
            ScriptInstance loaded = Assert.Single(storage.LoadScripts());
            Assert.Equal(script.JudgeScript, loaded.JudgeScript);
            Assert.True(loaded.JudgeScriptEnabled);
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void SpecializedScript_DoesNotPersistPluginDerivedFields()
    {
        string root = MakeTempDir();
        try
        {
            var storage = new ScriptStorage(root);
            storage.SaveScripts(new List<ScriptInstance>
            {
                new()
                {
                    Id = "specialized-record",
                    Name = "专项脚本",
                    PluginType = "fixture-plugin",
                    RootPath = root,
                    MainExe = "old.exe",
                    Args = "--old",
                    ConfigPath = "old.json",
                    LogPath = "old.log",
                    SuccessKeywords = "old-success",
                    FailureKeywords = "old-failure",
                    JudgeScriptEnabled = true,
                    JudgeScriptLanguage = "python",
                    JudgeScript = "old judge",
                    AutoUpdateConfig = false,
                    LaunchGame = true,
                    GameWaitSeconds = 12,
                },
            });

            JsonArray rootNode = JsonNode.Parse(File.ReadAllText(storage.ScriptsPath))!.AsArray();
            JsonObject record = Assert.IsType<JsonObject>(Assert.Single(rootNode));
            Assert.Equal("fixture-plugin", record["PluginType"]!.ToString());
            Assert.Equal(root, record["RootPath"]!.ToString());
            foreach (string property in new[]
            {
                "MainExe", "Args", "ConfigPath", "LogPath", "SuccessKeywords", "FailureKeywords",
                "JudgeScriptEnabled", "JudgeScriptLanguage", "JudgeScript", "AutoUpdateConfig",
            })
            {
                Assert.Null(record.FirstOrDefault(pair =>
                    string.Equals(pair.Key, property, StringComparison.OrdinalIgnoreCase)).Value);
            }

            ScriptInstance loaded = Assert.Single(storage.LoadScripts());
            Assert.Equal("fixture-plugin", loaded.PluginType);
            Assert.Empty(loaded.MainExe);
            Assert.Empty(loaded.ConfigPath);
            Assert.True(loaded.AutoUpdateConfig);
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void LegacyScriptsFile_MigratesInlineJudgeAndSpecializedSnapshotIdempotently()
    {
        string root = MakeTempDir();
        try
        {
            var storage = new ScriptStorage(root);
            string legacyUser = "legacy-user";
            string legacyStore = Path.Combine(root, "data", "legacy-special", legacyUser, "store");
            Directory.CreateDirectory(legacyStore);
            File.WriteAllText(Path.Combine(legacyStore, "config.json"), "{\"legacy\":true}");
            Directory.CreateDirectory(Path.GetDirectoryName(storage.ScriptsPath)!);
            var legacy = new JsonArray
            {
                new JsonObject
                {
                    ["Id"] = "legacy-generic",
                    ["Name"] = "旧通用",
                    ["JudgeScriptEnabled"] = true,
                    ["JudgeScriptLanguage"] = "python",
                    ["JudgeScript"] = "print('{\"status\":\"success\"}')",
                },
                new JsonObject
                {
                    ["Id"] = "legacy-special",
                    ["Name"] = "旧专项",
                    ["PluginType"] = "fixture-plugin",
                    ["RootPath"] = root,
                    ["MainExe"] = "stale.exe",
                    ["ConfigPath"] = "stale.json",
                    ["JudgeScriptEnabled"] = true,
                    ["JudgeScript"] = "stale plugin judge",
                },
            };
            File.WriteAllText(storage.ScriptsPath, legacy.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            List<ScriptInstance> first = storage.LoadScripts();
            Assert.Equal(2, first.Count);
            Assert.Equal("print('{\"status\":\"success\"}')", first[0].JudgeScript);
            Assert.Equal("python", first[0].JudgeScriptLanguage);
            Assert.Empty(first[1].MainExe);
            Assert.Empty(first[1].JudgeScript);
            JsonArray migratedRecords = JsonNode.Parse(File.ReadAllText(storage.ScriptsPath))!.AsArray();
            JsonObject migratedGeneric = Assert.IsType<JsonObject>(migratedRecords[0]);
            Assert.Null(migratedGeneric.FirstOrDefault(pair =>
                string.Equals(pair.Key, "JudgeScript", StringComparison.OrdinalIgnoreCase)).Value);
            Assert.True(File.Exists(storage.JudgeScripts.GetPath("legacy-generic", "python")));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "config", "migrations", "v0.13.0"), "scripts.json", SearchOption.AllDirectories));
            ConfigStoreMetadata? legacyMetadata = JsonSerializer.Deserialize<ConfigStoreMetadata>(
                File.ReadAllText(Path.Combine(root, "data", "legacy-special", legacyUser, "store-meta.json")),
                JsonOpts.Default);
            Assert.NotNull(legacyMetadata);
            Assert.Equal(ConfigStoreMetadata.HashLocator("stale.json"), legacyMetadata!.ConfigLocatorHash);

            List<ScriptInstance> second = storage.LoadScripts();
            Assert.Equal(first[0].JudgeScript, second[0].JudgeScript);
            Assert.Equal(first[1].PluginType, second[1].PluginType);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "config", "migrations", "v0.13.0"), "scripts.json", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void ChangingGenericJudgeLanguage_QuarantinesOldAsset()
    {
        string root = MakeTempDir();
        try
        {
            var storage = new ScriptStorage(root);
            var script = new ScriptInstance
            {
                Id = "language-switch",
                JudgeScriptEnabled = true,
                JudgeScriptLanguage = "javascript",
                JudgeScript = "js source",
            };
            storage.SaveScripts(new List<ScriptInstance> { script });
            script.JudgeScriptLanguage = "python";
            script.JudgeScript = "py source";
            storage.SaveScripts(new List<ScriptInstance> { script });

            Assert.False(File.Exists(storage.JudgeScripts.GetPath(script.Id, "javascript")));
            Assert.Equal("py source", File.ReadAllText(storage.JudgeScripts.GetPath(script.Id, "python")));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "config", "judge-scripts", "orphaned"), "language-switch.js", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void FailedScriptsManifestWrite_RestoresPreviousJudgeAsset()
    {
        string root = MakeTempDir();
        try
        {
            var storage = new ScriptStorage(root);
            var script = new ScriptInstance
            {
                Id = "manifest-write-failure",
                Name = "清单写入失败",
                JudgeScriptEnabled = true,
                JudgeScriptLanguage = "javascript",
                JudgeScript = "old source",
            };
            storage.SaveScripts(new List<ScriptInstance> { script });
            string assetPath = storage.JudgeScripts.GetPath(script.Id, "javascript");

            File.Delete(storage.ScriptsPath);
            Directory.CreateDirectory(storage.ScriptsPath);
            script.JudgeScript = "new source";

            Assert.ThrowsAny<Exception>(() => storage.SaveScripts(new List<ScriptInstance> { script }));
            Assert.Equal("old source", File.ReadAllText(assetPath));
            Assert.False(File.Exists(assetPath + ".tmp"));
            Assert.False(File.Exists(storage.ScriptsPath + ".tmp"));
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void CandidateResolutionUsesInlineJudgeBeforeExistingAsset()
    {
        string root = MakeTempDir();
        try
        {
            var judgeStore = new JudgeScriptStore(Path.Combine(root, "judge-scripts"));
            judgeStore.SaveAtomic("candidate-source", "javascript", "old source");
            var resolver = new ScriptSpecResolver(
                new MutableProfileResolver(),
                new TestPluginAvailability(),
                judgeStore);
            var candidate = new ScriptInstance
            {
                Id = "candidate-source",
                Name = "候选判断",
                JudgeScriptEnabled = true,
                JudgeScriptLanguage = "javascript",
                JudgeScript = "new source",
            };

            ResolvedScriptSpec resolved = resolver.ResolveCandidate(candidate);

            Assert.True(resolved.Succeeded);
            Assert.Equal("new source", resolved.Script.JudgeScript);
            Assert.NotEqual("old source", resolved.Script.JudgeScript);
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void CandidateResolutionDoesNotReuseOldAssetWhenEnabledSourceIsCleared()
    {
        string root = MakeTempDir();
        try
        {
            var judgeStore = new JudgeScriptStore(Path.Combine(root, "judge-scripts"));
            judgeStore.SaveAtomic("candidate-clear", "javascript", "old source");
            var resolver = new ScriptSpecResolver(
                new MutableProfileResolver(),
                new TestPluginAvailability(),
                judgeStore);
            var candidate = new ScriptInstance
            {
                Id = "candidate-clear",
                Name = "清空判断",
                JudgeScriptEnabled = true,
                JudgeScriptLanguage = "javascript",
                JudgeScript = "",
            };

            ResolvedScriptSpec resolved = resolver.ResolveCandidate(candidate);

            Assert.False(resolved.Succeeded);
            Assert.Contains("判断脚本资产不存在", resolved.Error, StringComparison.Ordinal);
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void LegacyMigration_PreservesConflictingJudgeAsset()
    {
        string root = MakeTempDir();
        try
        {
            var storage = new ScriptStorage(root);
            storage.JudgeScripts.SaveAtomic("legacy-conflict", "javascript", "asset source");
            Directory.CreateDirectory(Path.GetDirectoryName(storage.ScriptsPath)!);
            var legacy = new JsonArray
            {
                new JsonObject
                {
                    ["Id"] = "legacy-conflict",
                    ["Name"] = "冲突迁移",
                    ["JudgeScriptEnabled"] = true,
                    ["JudgeScriptLanguage"] = "javascript",
                    ["JudgeScript"] = "inline source",
                },
            };
            File.WriteAllText(storage.ScriptsPath, legacy.ToJsonString());

            List<ScriptInstance> loaded = storage.LoadScripts();

            Assert.Equal("inline source", Assert.Single(loaded).JudgeScript);
            Assert.Equal("inline source", File.ReadAllText(storage.JudgeScripts.GetPath("legacy-conflict", "javascript")));
            Assert.NotEmpty(Directory.GetFiles(
                Path.Combine(root, "config", "judge-scripts", "orphaned"),
                "legacy-conflict.js",
                SearchOption.AllDirectories));
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void ConfigPathChange_KeepsOldStoreUntilNewLocationExists()
    {
        string root = MakeTempDir();
        string scriptId = "metadata-path-" + Guid.NewGuid().ToString("N");
        string userId = "user-" + Guid.NewGuid().ToString("N");
        string oldConfig = Path.Combine(root, "old.json");
        string newConfig = Path.Combine(root, "new.json");
        try
        {
            ConfigSwapSession.ConfigureRecovery(_ => null, () => []);
            File.WriteAllText(oldConfig, "{\"state\":\"old\"}");
            bool prepared = UserConfigManager.PrepareForRun(
                scriptId,
                userId,
                oldConfig,
                out string? prepareError);
            Assert.True(prepared, prepareError);
            Assert.Null(UserConfigManager.RestoreAfterRun(scriptId, userId, oldConfig));
            Assert.True(File.Exists(oldConfig));
            Assert.NotNull(ConfigStoreMetadata.Load(scriptId, userId));

            bool reused = UserConfigManager.PrepareForRun(
                scriptId,
                userId,
                newConfig,
                out string? changedPathError);

            Assert.False(reused);
            Assert.Contains("不存在", changedPathError, StringComparison.Ordinal);
            Assert.Equal("{\"state\":\"old\"}", File.ReadAllText(Path.Combine(
                ConfigSwapPaths.StoreDir(scriptId, userId), "old.json")));
            Assert.False(Directory.Exists(ConfigSwapPaths.StoreArchiveDir(scriptId, userId)));

            File.WriteAllText(newConfig, "{\"state\":\"new\"}");
            bool rebound = UserConfigManager.PrepareForRun(
                scriptId,
                userId,
                newConfig,
                out string? reboundError);
            Assert.True(rebound, reboundError);
            Assert.Equal("{\"state\":\"new\"}", File.ReadAllText(newConfig));
            Assert.Null(UserConfigManager.RestoreAfterRun(scriptId, userId, newConfig));
            Assert.Equal("{\"state\":\"new\"}", File.ReadAllText(Path.Combine(
                ConfigSwapPaths.StoreDir(scriptId, userId), "new.json")));
            Assert.False(Directory.Exists(ConfigSwapPaths.StoreRebindDir(scriptId, userId)));
        }
        finally
        {
            DeleteExact(root);
            ConfigSwapPrimitives.TryDeleteDir(Path.Combine(AppPaths.DataDir, scriptId));
        }
    }

    [Fact]
    public void RebindRecovery_PartialNewStoreRestoresOldSnapshot()
    {
        string scriptId = "metadata-rebind-recovery-" + Guid.NewGuid().ToString("N");
        string userId = "user-" + Guid.NewGuid().ToString("N");
        string oldConfig = Path.Combine(Path.GetTempPath(), "old-" + Guid.NewGuid().ToString("N") + ".json");
        string newConfig = Path.Combine(Path.GetTempPath(), "new-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(oldConfig, "{\"state\":\"old\"}");
            ConfigStoreMetadata oldMetadata = ConfigStoreMetadata.For(oldConfig);
            ConfigStoreMetadata newMetadata = ConfigStoreMetadata.For(newConfig);
            string store = ConfigSwapPaths.StoreDir(scriptId, userId);
            string rebindDir = ConfigSwapPaths.StoreRebindDir(scriptId, userId);
            Directory.CreateDirectory(store);
            File.WriteAllText(Path.Combine(store, "old.json"), "old");
            ConfigStoreMetadata.Save(scriptId, userId, oldMetadata);
            Directory.CreateDirectory(rebindDir);
            Directory.Move(store, ConfigSwapPaths.StoreRebindOldDir(scriptId, userId));
            File.Move(
                ConfigSwapPaths.StoreMetadataPath(scriptId, userId),
                Path.Combine(rebindDir, "old-store-meta.json"));
            Directory.CreateDirectory(store);
            File.WriteAllText(Path.Combine(store, "partial.json"), "partial");
            JsonUtil.WriteAtomic(
                ConfigSwapPaths.StoreRebindNewMetadataPath(scriptId, userId),
                JsonSerializer.Serialize(newMetadata, JsonOpts.Indented));

            ConfigStoreMetadata.RecoverRebind(scriptId, userId);

            Assert.Equal("old", File.ReadAllText(Path.Combine(store, "old.json")));
            Assert.False(File.Exists(Path.Combine(store, "partial.json")));
            Assert.NotNull(ConfigStoreMetadata.Load(scriptId, userId));
            Assert.False(Directory.Exists(rebindDir));
        }
        finally
        {
            ConfigSwapPrimitives.TryDeleteDir(Path.Combine(AppPaths.DataDir, scriptId));
            TryDeleteFile(oldConfig);
            TryDeleteFile(newConfig);
        }
    }

    [Fact]
    public void RebindRecovery_CommittedNewStoreCleansIsolation()
    {
        string scriptId = "metadata-rebind-commit-" + Guid.NewGuid().ToString("N");
        string userId = "user-" + Guid.NewGuid().ToString("N");
        string newConfig = Path.Combine(Path.GetTempPath(), "new-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ConfigStoreMetadata expected = ConfigStoreMetadata.For(newConfig);
            string store = ConfigSwapPaths.StoreDir(scriptId, userId);
            string rebindDir = ConfigSwapPaths.StoreRebindDir(scriptId, userId);
            Directory.CreateDirectory(store);
            File.WriteAllText(Path.Combine(store, "new.json"), "new");
            ConfigStoreMetadata.Save(scriptId, userId, expected);
            Directory.CreateDirectory(ConfigSwapPaths.StoreRebindOldDir(scriptId, userId));
            File.WriteAllText(Path.Combine(ConfigSwapPaths.StoreRebindOldDir(scriptId, userId), "old.json"), "old");
            JsonUtil.WriteAtomic(
                ConfigSwapPaths.StoreRebindNewMetadataPath(scriptId, userId),
                JsonSerializer.Serialize(expected, JsonOpts.Indented));

            ConfigStoreMetadata.RecoverRebind(scriptId, userId);

            Assert.Equal("new", File.ReadAllText(Path.Combine(store, "new.json")));
            Assert.False(Directory.Exists(rebindDir));
        }
        finally
        {
            ConfigSwapPrimitives.TryDeleteDir(Path.Combine(AppPaths.DataDir, scriptId));
            TryDeleteFile(newConfig);
        }
    }

    [Fact]
    public void ResolverReadsCurrentSpecializedProfileOnEveryResolution()
    {
        string root = MakeTempDir();
        try
        {
            var capability = new MutableProfileResolver();
            var availability = new TestPluginAvailability();
            var resolver = new ScriptSpecResolver(
                capability,
                availability,
                new JudgeScriptStore(Path.Combine(root, "judge-scripts")));
            var declaration = new ScriptInstance
            {
                Id = "dynamic-profile",
                PluginType = "fixture-plugin",
                RootPath = root,
            };

            capability.Profile = new ScriptProfile
            {
                MainExe = "new-a.exe",
                ConfigPath = "new-a.json",
                LogPath = "new-a.log",
                JudgeScript = "judge-a",
                JudgeScriptLanguage = "javascript",
                PluginName = "fixture-plugin",
                PluginVersion = "1.0.0",
                JudgeScriptPath = "plugin/data/judge.js",
            };
            ResolvedScriptSpec first = resolver.Resolve(declaration);
            capability.Profile = new ScriptProfile
            {
                MainExe = "new-b.exe",
                ConfigPath = "new-b.json",
                LogPath = "new-b.log",
                JudgeScript = "judge-b",
                JudgeScriptLanguage = "python",
                PluginName = "fixture-plugin",
                PluginVersion = "2.0.0",
                JudgeScriptPath = "plugin-v2/data/judge.py",
            };
            ResolvedScriptSpec second = resolver.Resolve(declaration);

            Assert.Equal("new-a.exe", first.Script.MainExe);
            Assert.Equal("new-b.exe", second.Script.MainExe);
            Assert.Equal("new-b.json", second.Script.ConfigPath);
            Assert.Equal("2.0.0", second.PluginVersion);
            Assert.Equal("plugin-file", second.JudgeScript.SourceKind);
            Assert.Equal("python", second.JudgeScript.Language);
            Assert.NotEqual(first.ProfileHash, second.ProfileHash);
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void SessionMark_UsesRedundantCopyWhenPrimaryIsCorrupt()
    {
        string scriptId = "mark-fallback-" + Guid.NewGuid().ToString("N");
        string userId = "user-" + Guid.NewGuid().ToString("N");
        try
        {
            var mark = new ConfigSessionMark
            {
                ScriptId = scriptId,
                UserName = userId,
                UserId = userId,
                ConfigPath = Path.Combine(Path.GetTempPath(), "fixture-config.json"),
                OriginalKind = "file",
                Phase = "run",
                LaunchExe = "fixture.exe",
                PluginName = "fixture-plugin",
                PluginVersion = "1.2.3",
                ProfileHash = "profile-hash",
            };
            mark.Write();
            File.WriteAllText(ConfigSessionMark.MarkFile(scriptId, userId), "{broken");

            ConfigSessionMark? recovered = ConfigSessionMark.TryRead(scriptId, userId);
            Assert.NotNull(recovered);
            Assert.Equal("fixture.exe", recovered!.LaunchExe);
            Assert.Equal("1.2.3", recovered.PluginVersion);
            Assert.Equal(userId, recovered.UserId);
        }
        finally
        {
            ConfigSessionMark.Clear(scriptId, userId);
            ConfigSwapPrimitives.TryDeleteDir(Path.Combine(AppPaths.DataDir, scriptId));
        }
    }

    [Fact]
    public void CorruptSessionMarks_PreserveConfigAndCacheWithoutGuessing()
    {
        string root = MakeTempDir();
        string scriptId = "mark-corrupt-" + Guid.NewGuid().ToString("N");
        string userId = "user-" + Guid.NewGuid().ToString("N");
        string configPath = Path.Combine(root, "config.json");
        try
        {
            ConfigSwapSession.ConfigureRecovery(_ => null, () => []);
            File.WriteAllText(configPath, "current config");
            string cache = ConfigSwapPaths.CacheDir(scriptId, userId);
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "original.json"), "original config");
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigSessionMark.MarkFile(scriptId, userId))!);
            File.WriteAllText(ConfigSessionMark.MarkFile(scriptId, userId), "{broken-primary");
            File.WriteAllText(ConfigSessionMark.BackupMarkFile(scriptId, userId), "{broken-backup");

            bool prepared = UserConfigManager.PrepareForRun(
                scriptId,
                userId,
                configPath,
                out string? error);

            Assert.False(prepared);
            Assert.Contains("标记", error, StringComparison.Ordinal);
            Assert.Equal("current config", File.ReadAllText(configPath));
            Assert.True(File.Exists(Path.Combine(cache, "original.json")));
            Assert.True(File.Exists(ConfigSessionMark.MarkFile(scriptId, userId)));
            Assert.True(File.Exists(ConfigSessionMark.BackupMarkFile(scriptId, userId)));
        }
        finally
        {
            DeleteExact(root);
            ConfigSwapPrimitives.TryDeleteDir(Path.Combine(AppPaths.DataDir, scriptId));
        }
    }

    [Fact]
    public void RestoreHiddenConfigs_PreservesDestinationConflict()
    {
        string root = MakeTempDir();
        string scriptId = "hidden-conflict-" + Guid.NewGuid().ToString("N");
        string userId = "user-" + Guid.NewGuid().ToString("N");
        string configPath = Path.Combine(root, "config", "main.json");
        try
        {
            string hidden = ConfigSwapPaths.HiddenConfigDir(scriptId, userId);
            Directory.CreateDirectory(hidden);
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            string hiddenFile = Path.Combine(hidden, "other.json");
            string destination = Path.Combine(Path.GetDirectoryName(configPath)!, "other.json");
            File.WriteAllText(hiddenFile, "hidden copy");
            File.WriteAllText(destination, "current copy");

            UserConfigManager.RestoreHiddenConfigs(scriptId, userId, configPath);

            Assert.Equal("current copy", File.ReadAllText(destination));
            Assert.Equal("hidden copy", File.ReadAllText(hiddenFile));
        }
        finally
        {
            DeleteExact(root);
            ConfigSwapPrimitives.TryDeleteDir(Path.Combine(AppPaths.DataDir, scriptId));
        }
    }

    private static string MakeTempDir()
    {
        string root = Path.Combine(Path.GetTempPath(), "np-script-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteExact(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed class MutableProfileResolver : IPluginCapabilityResolver
    {
        public ScriptProfile Profile { get; set; } = new();

        public bool SupportsEmulator(string pluginName) => false;

        public ScriptProfile? ResolveProfile(string pluginName, string rootPath) => Profile;
    }

    private sealed class TestPluginAvailability : IPluginAvailability
    {
        public bool IsKnownPlugin(string pluginName) => true;

        public bool IsDataSpecializedPlugin(string pluginName) => true;

        public bool IsEnabled(string pluginName) => true;
    }
}
