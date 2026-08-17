using System.Text.Json.Nodes;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>自动更新配置（v0.7.6）：还原描述执行器（array/map/路径无效/未覆盖键保持）、
/// 全量镜像同步（新增/删除/插队跳过/启停还原）、有效性校验（空/骤降跳过）、首次检测时机判定。</summary>
public class ConfigSwapSyncTests
{
    private static string MakeTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "np-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ConfigSwapSession.ToggleRestore ArrayToggle(string path, Dictionary<string, bool> initial, string keyField = "id", string enabledField = "enabled")
    {
        return new ConfigSwapSession.ToggleRestore { Type = "array", Path = path, KeyField = keyField, EnabledField = enabledField, Initial = initial };
    }

    private static ConfigSwapSession.ToggleRestore MapToggle(string path, Dictionary<string, bool> initial)
    {
        return new ConfigSwapSession.ToggleRestore { Type = "map", Path = path, Initial = initial };
    }

    /* ---------------- 首次检测时机（ShouldRunFirstSync） ---------------- */

    [Fact]
    public void ShouldRunFirstSync_ThresholdBoundary()
    {
        Assert.False(RunSession.ShouldRunFirstSync(0, 15));
        Assert.False(RunSession.ShouldRunFirstSync(14.9, 15));
        Assert.True(RunSession.ShouldRunFirstSync(15, 15));
        Assert.True(RunSession.ShouldRunFirstSync(20, 1));
        Assert.True(RunSession.ShouldRunFirstSync(1, 1));
    }

    [Fact]
    public void RestoreKind_MissingInfersFileOrDirectoryFromConfigPath()
    {
        Assert.Equal(PathKind.File, ConfigSwapPrimitives.RestoreKind(new ConfigSessionMark { ConfigPath = "C:\\cfg\\state.json", OriginalKind = "missing" }));
        Assert.Equal(PathKind.Dir, ConfigSwapPrimitives.RestoreKind(new ConfigSessionMark { ConfigPath = "C:\\cfg\\config", OriginalKind = "missing" }));
    }

    /* ---------------- 还原描述路径 DSL（LocateNode） ---------------- */

    [Fact]
    public void LocateNode_ArrayPath_Resolves()
    {
        JsonNode root = JsonNode.Parse("{\"instances\":[{\"id\":\"a\",\"tasks\":[{\"id\":\"t1\"}]},{\"id\":\"b\",\"tasks\":[]}]}")!;
        JsonNode? tasks = ConfigSwapSession.LocateNode(root, "instances[0].tasks");
        Assert.NotNull(tasks);
        Assert.IsType<JsonArray>(tasks);
        Assert.Single((JsonArray)tasks!);
    }

    [Fact]
    public void LocateNode_InvalidPath_ReturnsNull()
    {
        JsonNode root = JsonNode.Parse("{\"instances\":[{\"tasks\":[]}]}")!;
        Assert.Null(ConfigSwapSession.LocateNode(root, "instances[9].tasks"));
        Assert.Null(ConfigSwapSession.LocateNode(root, "missing.tasks"));
        Assert.Null(ConfigSwapSession.LocateNode(root, "instances[0].missing"));
        Assert.Null(ConfigSwapSession.LocateNode(root, "instances[abc].tasks"));
    }

    [Fact]
    public void LocateNode_ArraySelector_ResolvesByStableId()
    {
        JsonNode root = JsonNode.Parse("{\"instances\":[{\"id\":\"first\",\"tasks\":[]},{\"id\":\"second\",\"tasks\":[{\"id\":\"t2\"}]}]}")!;
        Assert.NotNull(ConfigSwapSession.LocateNode(root, "instances"));
        Assert.NotNull(ConfigSwapSession.LocateNode(root, "instances[id=second]"));
        JsonNode? tasks = ConfigSwapSession.LocateNode(root, "instances[id=second].tasks");
        Assert.NotNull(tasks);
        Assert.Single((JsonArray)tasks!);
        Assert.Equal("t2", ((JsonArray)tasks!)[0]!["id"]!.ToString());
    }

    /* ---------------- 还原描述执行器（ApplyToggle） ---------------- */

    [Fact]
    public void ApplyToggle_Array_RestoresInitial_AndKeepsUnlisted()
    {
        string content = "{\"instances\":[{\"tasks\":[{\"id\":\"t1\",\"enabled\":false},{\"id\":\"t2\",\"enabled\":false},{\"id\":\"t3\",\"enabled\":true}]}]}";
        bool ok = ConfigSwapSession.ApplyToggle(ref content, ArrayToggle("instances[0].tasks", new Dictionary<string, bool> { ["t1"] = true, ["t2"] = true }));
        Assert.True(ok);
        JsonObject root = (JsonObject)JsonNode.Parse(content)!;
        JsonArray tasks = (JsonArray)root["instances"]![0]!["tasks"]!;
        Assert.True((bool)tasks[0]!["enabled"]!);
        Assert.True((bool)tasks[1]!["enabled"]!);
        Assert.True((bool)tasks[2]!["enabled"]!);
    }

    [Fact]
    public void ApplyToggle_Map_RestoresKeys_AndKeepsUnlisted()
    {
        string content = "{\"TaskEnabledList\":{\"g1\":false,\"g2\":false,\"g3\":true}}";
        bool ok = ConfigSwapSession.ApplyToggle(ref content, MapToggle("TaskEnabledList", new Dictionary<string, bool> { ["g1"] = true, ["g2"] = true }));
        Assert.True(ok);
        JsonObject obj = (JsonObject)JsonNode.Parse(content)!["TaskEnabledList"]!;
        Assert.True((bool)obj["g1"]!);
        Assert.True((bool)obj["g2"]!);
        Assert.True((bool)obj["g3"]!);
    }

    [Fact]
    public void ApplyToggle_InvalidTarget_ReturnsFalse()
    {
        string content = "{\"tasks\":[{\"id\":\"t1\",\"enabled\":false}]}";
        Assert.False(ConfigSwapSession.ApplyToggle(ref content, ArrayToggle("missing.tasks", new Dictionary<string, bool> { ["t1"] = true })));
        Assert.False(ConfigSwapSession.ApplyToggle(ref content, MapToggle("TaskEnabledList", new Dictionary<string, bool> { ["g1"] = true })));
        string objContent = "{\"tasks\":{\"id\":\"t1\"}}";
        Assert.False(ConfigSwapSession.ApplyToggle(ref objContent, ArrayToggle("tasks", new Dictionary<string, bool> { ["t1"] = true })));
        string bad = "not-json{";
        Assert.False(ConfigSwapSession.ApplyToggle(ref bad, ArrayToggle("tasks", new Dictionary<string, bool> { ["t1"] = true })));
    }

    /* ---------------- 还原描述解析（ReadRestoreDescriptor） ---------------- */

    [Fact]
    public void ReadRestoreDescriptor_MissingFile_ReturnsNull()
    {
        Assert.Null(ConfigSwapSession.ReadRestoreDescriptor(MakeTempDir()));
    }

    [Fact]
    public void ReadRestoreDescriptor_Valid_And_Malformed()
    {
        string dir = MakeTempDir();
        File.WriteAllText(Path.Combine(dir, "config-restore.json"),
            "{\"files\":[{\"file\":\"mxu-MaaEnd.json\",\"toggles\":[{\"type\":\"array\",\"path\":\"instances[0].tasks\",\"keyField\":\"id\",\"enabledField\":\"enabled\",\"initial\":{\"t1\":true,\"t2\":false}}]}]}");
        ConfigSwapSession.ConfigRestoreDescriptor? descriptor = ConfigSwapSession.ReadRestoreDescriptor(dir);
        Assert.NotNull(descriptor);
        Assert.Single(descriptor!.Files);
        Assert.Equal("mxu-MaaEnd.json", descriptor.Files[0].File);
        Assert.Single(descriptor.Files[0].Toggles);
        Assert.Equal("array", descriptor.Files[0].Toggles[0].Type);
        Assert.True(descriptor.Files[0].Toggles[0].Initial["t1"]);
        Assert.False(descriptor.Files[0].Toggles[0].Initial["t2"]);

        string badDir = MakeTempDir();
        File.WriteAllText(Path.Combine(badDir, "config-restore.json"), "not-json");
        Assert.Null(ConfigSwapSession.ReadRestoreDescriptor(badDir));
    }

    /* ---------------- 全量镜像（MirrorToStore） ---------------- */

    [Fact]
    public void MirrorToStore_CopyAndPrune()
    {
        string cfg = MakeTempDir();
        string store = MakeTempDir();
        Directory.CreateDirectory(Path.Combine(cfg, "sub"));
        File.WriteAllText(Path.Combine(cfg, "a.txt"), "A");
        File.WriteAllText(Path.Combine(cfg, "sub", "b.txt"), "B");
        File.WriteAllText(Path.Combine(store, "a.txt"), "OLD-A");
        File.WriteAllText(Path.Combine(store, "stale.txt"), "STALE");

        (int written, int deleted) = ConfigSwapSession.MirrorToStore(cfg, store, new HashSet<string>(), null);

        Assert.Equal(2, written);
        Assert.Equal(1, deleted);
        Assert.Equal("A", File.ReadAllText(Path.Combine(store, "a.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(store, "sub", "b.txt")));
        Assert.False(File.Exists(Path.Combine(store, "stale.txt")));
    }

    [Fact]
    public void MirrorToStoreAtomic_CommitsAndKeepsPreviousSnapshot()
    {
        string cfg = MakeTempDir();
        string store = Path.Combine(MakeTempDir(), "store");
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(cfg, "state.txt"), "NEW");
        File.WriteAllText(Path.Combine(store, "state.txt"), "OLD");

        (int written, int preserved) = ConfigSwapSession.MirrorToStoreAtomic(
            cfg,
            store,
            new HashSet<string>(),
            null,
            ConfigSwapSession.SampleConfig(cfg));

        Assert.Equal(1, written);
        Assert.Equal(0, preserved);
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(store, "state.txt")));
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(store + ".previous", "state.txt")));
        Assert.False(Directory.Exists(store + ".tmp"));
    }

    [Fact]
    public void MirrorToStoreAtomic_SourceChangedAbortsAndKeepsOldStore()
    {
        string cfg = MakeTempDir();
        string store = Path.Combine(MakeTempDir(), "store");
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(cfg, "state.txt"), "NEW");
        File.WriteAllText(Path.Combine(store, "state.txt"), "OLD");

        Assert.Throws<IOException>(() => ConfigSwapSession.MirrorToStoreAtomic(
            cfg,
            store,
            new HashSet<string>(),
            null,
            "stale-sample"));

        Assert.Equal("OLD", File.ReadAllText(Path.Combine(store, "state.txt")));
        Assert.False(Directory.Exists(store + ".tmp"));
    }

    [Fact]
    public void MirrorToStore_SwapFileWithoutDescriptor_Skipped()
    {
        string cfg = MakeTempDir();
        string store = MakeTempDir();
        File.WriteAllText(Path.Combine(cfg, "swap.json"), "{\"enabled\":false}");
        File.WriteAllText(Path.Combine(store, "swap.json"), "{\"enabled\":true}");

        (int written, int _) = ConfigSwapSession.MirrorToStore(cfg, store, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "swap.json" }, null);

        Assert.Equal(0, written);
        Assert.Equal("{\"enabled\":true}", File.ReadAllText(Path.Combine(store, "swap.json")));
    }

    [Fact]
    public void MirrorToStore_SwapFileWithDescriptor_RestoresToggleThenWrites()
    {
        string cfg = MakeTempDir();
        string store = MakeTempDir();
        File.WriteAllText(Path.Combine(cfg, "mxu-MaaEnd.json"), "{\"instances\":[{\"tasks\":[{\"id\":\"t1\",\"enabled\":false,\"count\":5},{\"id\":\"t2\",\"enabled\":true}]}]}");
        File.WriteAllText(Path.Combine(store, "mxu-MaaEnd.json"), "{\"instances\":[{\"tasks\":[{\"id\":\"t1\",\"enabled\":true},{\"id\":\"t2\",\"enabled\":true}]}]}");

        var descriptor = new ConfigSwapSession.ConfigRestoreDescriptor();
        descriptor.Files.Add(new ConfigSwapSession.FileRestore
        {
            File = "mxu-MaaEnd.json",
            Toggles = new List<ConfigSwapSession.ToggleRestore> { ArrayToggle("instances[0].tasks", new Dictionary<string, bool> { ["t1"] = true, ["t2"] = true }) },
        });
        (int written, int _) = ConfigSwapSession.MirrorToStore(cfg, store, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mxu-MaaEnd.json" }, descriptor);

        Assert.Equal(1, written);
        JsonObject stored = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(store, "mxu-MaaEnd.json")))!;
        JsonArray tasks = (JsonArray)stored["instances"]![0]!["tasks"]!;
        Assert.True((bool)tasks[0]!["enabled"]!);
        Assert.Equal(5, (int)tasks[0]!["count"]!);
        Assert.True((bool)tasks[1]!["enabled"]!);
    }

    [Fact]
    public void MirrorToStore_NonSwapFileWithDescriptorEntry_Ignored()
    {
        string cfg = MakeTempDir();
        string store = MakeTempDir();
        File.WriteAllText(Path.Combine(cfg, "a.txt"), "NEW");
        File.WriteAllText(Path.Combine(store, "a.txt"), "OLD");

        var descriptor = new ConfigSwapSession.ConfigRestoreDescriptor();
        descriptor.Files.Add(new ConfigSwapSession.FileRestore
        {
            File = "a.txt",
            Toggles = new List<ConfigSwapSession.ToggleRestore> { MapToggle("TaskEnabledList", new Dictionary<string, bool> { ["g1"] = true }) },
        });
        // 描述存在但 a.txt 不在插队清单 → 全量镜像直接复制（还原描述仅作用于插队文件）。
        (int written, int _) = ConfigSwapSession.MirrorToStore(cfg, store, new HashSet<string>(), descriptor);

        Assert.Equal(1, written);
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(store, "a.txt")));
    }

    /* ---------------- 有效性校验（ValidForSync） ---------------- */

    [Fact]
    public void ValidForSync_MissingOrEmpty_Skips()
    {
        string missing = Path.Combine(MakeTempDir(), "nope");
        Assert.False(ConfigSwapSession.ValidForSync(missing, MakeTempDir()));

        string emptyFile = Path.Combine(MakeTempDir(), "e.json");
        File.WriteAllText(emptyFile, "");
        Assert.False(ConfigSwapSession.ValidForSync(emptyFile, MakeTempDir()));

        string okFile = Path.Combine(MakeTempDir(), "ok.json");
        File.WriteAllText(okFile, "{}");
        Assert.True(ConfigSwapSession.ValidForSync(okFile, MakeTempDir()));

        string emptyDir = MakeTempDir();
        Assert.False(ConfigSwapSession.ValidForSync(emptyDir, MakeTempDir()));

        string okDir = MakeTempDir();
        File.WriteAllText(Path.Combine(okDir, "x.txt"), "X");
        Assert.True(ConfigSwapSession.ValidForSync(okDir, MakeTempDir()));
    }

    [Fact]
    public void ValidForSync_FileCountDrop_Skips()
    {
        string cfg = MakeTempDir();
        string store = MakeTempDir();
        File.WriteAllText(Path.Combine(cfg, "a.txt"), "A");
        for (int i = 0; i < 4; i++)
        {
            File.WriteAllText(Path.Combine(store, "s" + i + ".txt"), "S");
        }
        Assert.False(ConfigSwapSession.ValidForSync(cfg, store));
    }

    /* ---------------- 内容有效性探测（v0.7.6 评估加强：半写/损坏不入库） ---------------- */

    [Fact]
    public void ValidForSync_BrokenJsonFile_Skips()
    {
        string broken = Path.Combine(MakeTempDir(), "cfg.json");
        File.WriteAllText(broken, "{\"tasks\":[{\"id\":\"t1\",\"enabled\":true");
        Assert.False(ConfigSwapSession.ValidForSync(broken, MakeTempDir()));

        string valid = Path.Combine(MakeTempDir(), "cfg.json");
        File.WriteAllText(valid, "{\"tasks\":[{\"id\":\"t1\",\"enabled\":true}]}");
        Assert.True(ConfigSwapSession.ValidForSync(valid, MakeTempDir()));

        string nonJson = Path.Combine(MakeTempDir(), "count.txt");
        File.WriteAllText(nonJson, "42");
        Assert.True(ConfigSwapSession.ValidForSync(nonJson, MakeTempDir()));
    }

    [Fact]
    public void ValidForSync_DirWithBrokenJson_Skips()
    {
        string cfg = MakeTempDir();
        File.WriteAllText(Path.Combine(cfg, "ok.txt"), "OK");
        File.WriteAllText(Path.Combine(cfg, "broken.json"), "{\"a\":");
        Assert.False(ConfigSwapSession.ValidForSync(cfg, MakeTempDir()));
    }

    [Fact]
    public void ValidForSync_DirMixedValidJsonAndText_Passes()
    {
        string cfg = MakeTempDir();
        File.WriteAllText(Path.Combine(cfg, "ok.json"), "{\"a\":1}");
        File.WriteAllText(Path.Combine(cfg, "log.txt"), "plain text");
        Assert.True(ConfigSwapSession.ValidForSync(cfg, MakeTempDir()));
    }

    [Fact]
    public void ValidForSync_EmptyJsonInDir_Skips_ButEmptyOtherFile_Passes()
    {
        string cfg = MakeTempDir();
        File.WriteAllText(Path.Combine(cfg, "empty.json"), "");
        Assert.False(ConfigSwapSession.ValidForSync(cfg, MakeTempDir()));

        string cfg2 = MakeTempDir();
        File.WriteAllText(Path.Combine(cfg2, "empty.txt"), "");
        File.WriteAllText(Path.Combine(cfg2, "ok.json"), "{}");
        Assert.True(ConfigSwapSession.ValidForSync(cfg2, MakeTempDir()));
    }

    [Fact]
    public void ValidForSync_JsonContentWithoutJsonExtension_Validated()
    {
        string cfg = MakeTempDir();
        File.WriteAllText(Path.Combine(cfg, "data.cfg"), "{\"a\":");
        Assert.False(ConfigSwapSession.ValidForSync(cfg, MakeTempDir()));

        string cfg2 = MakeTempDir();
        File.WriteAllText(Path.Combine(cfg2, "data.cfg"), "{\"a\":1}");
        Assert.True(ConfigSwapSession.ValidForSync(cfg2, MakeTempDir()));
    }
}
