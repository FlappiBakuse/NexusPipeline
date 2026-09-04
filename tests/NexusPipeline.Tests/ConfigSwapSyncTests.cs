using System.Text.Json.Nodes;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>自动更新配置：还原描述执行器（array/map/路径无效/未覆盖键保持）、
/// 增量事务有效性校验（空/骤降跳过）、首次检测时机判定。</summary>
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
    private static ConfigSwapSession.ToggleRestore BoolArrayToggle(string path, List<bool> initial)
    {
        return new ConfigSwapSession.ToggleRestore { Type = "boolArray", Path = path, InitialList = initial };
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
        Assert.Equal(PathKind.File, ConfigSwapPrimitives.RestoreKind(new ConfigSessionMark { ConfigPath = "C:\\cfg\\state.json", ConfigKind = "missing" }));
        Assert.Equal(PathKind.Dir, ConfigSwapPrimitives.RestoreKind(new ConfigSessionMark { ConfigPath = "C:\\cfg\\config", ConfigKind = "missing" }));
    }

    [Fact]
    public void RestoreConfigReplacements_CorruptMeta_IsQuarantined()
    {
        string scriptId = "kn74-" + Guid.NewGuid().ToString("N");
        string userName = "user";
        string backupDir = ConfigSwapPaths.ReplaceBackupDir(scriptId, userName);
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, ".meta"), "{not-json");
        string parent = Directory.GetParent(backupDir)!.FullName;
        try
        {
            Assert.False(ConfigSwapSession.RestoreConfigReplacements(scriptId, userName));
            Assert.False(Directory.Exists(backupDir));
            Assert.Single(Directory.GetDirectories(parent, Path.GetFileName(backupDir) + ".corrupt-*"));
        }
        finally
        {
            if (Directory.Exists(Path.Combine(AppPaths.DataDir, scriptId)))
            {
                Directory.Delete(Path.Combine(AppPaths.DataDir, scriptId), recursive: true);
            }
        }
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

    [Fact]
    public void ApplyToggle_BoolArray_RestoresByIndex_AndKeepsExtras()
    {
        string content = "{\"TASK_ORDER_GROUP\":{\"ALL_PIPELINES\":[{\"TASK_PIPELINE\":[\"A\",\"B\",\"C\"],\"TASK_ONOFF\":[false,false,false]}]}}";
        bool ok = ConfigSwapSession.ApplyToggle(ref content, BoolArrayToggle("TASK_ORDER_GROUP.ALL_PIPELINES[0].TASK_ONOFF", new List<bool> { true, false, true }));
        Assert.True(ok);
        JsonArray onoff = (JsonArray)JsonNode.Parse(content)!["TASK_ORDER_GROUP"]!["ALL_PIPELINES"]![0]!["TASK_ONOFF"]!;
        Assert.True((bool)onoff[0]!);
        Assert.False((bool)onoff[1]!);
        Assert.True((bool)onoff[2]!);
    }

    [Fact]
    public void ApplyToggle_BoolArray_ShortTargetFails_LongTargetKeepsExtras()
    {
        string shortContent = "{\"onoff\":[false]}";
        Assert.False(ConfigSwapSession.ApplyToggle(ref shortContent, BoolArrayToggle("onoff", new List<bool> { true, true })));

        string longContent = "{\"onoff\":[false,false,false]}";
        Assert.True(ConfigSwapSession.ApplyToggle(ref longContent, BoolArrayToggle("onoff", new List<bool> { true })));
        JsonArray onoff = (JsonArray)JsonNode.Parse(longContent)!["onoff"]!;
        Assert.True((bool)onoff[0]!);
        Assert.False((bool)onoff[1]!);
        Assert.False((bool)onoff[2]!);
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

    [Fact]
    public void ReadRestoreDescriptor_BoolArray_ParsesInitialList_AndDropsEmpty()
    {
        string dir = MakeTempDir();
        File.WriteAllText(Path.Combine(dir, "config-restore.json"),
            "{\"files\":[{\"file\":\"task.json\",\"toggles\":[{\"type\":\"boolArray\",\"path\":\"TASK_ORDER_GROUP.ALL_PIPELINES[0].TASK_ONOFF\",\"initial\":[true,false,true]}]}]}");
        ConfigSwapSession.ConfigRestoreDescriptor? descriptor = ConfigSwapSession.ReadRestoreDescriptor(dir);
        Assert.NotNull(descriptor);
        Assert.Single(descriptor!.Files);
        Assert.Equal("task.json", descriptor.Files[0].File);
        Assert.Single(descriptor.Files[0].Toggles);
        Assert.Equal("boolArray", descriptor.Files[0].Toggles[0].Type);
        Assert.Equal(new List<bool> { true, false, true }, descriptor.Files[0].Toggles[0].InitialList);

        string emptyDir = MakeTempDir();
        File.WriteAllText(Path.Combine(emptyDir, "config-restore.json"),
            "{\"files\":[{\"file\":\"task.json\",\"toggles\":[{\"type\":\"boolArray\",\"path\":\"a.b\",\"initial\":[]}]}]}");
        Assert.Null(ConfigSwapSession.ReadRestoreDescriptor(emptyDir));
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
