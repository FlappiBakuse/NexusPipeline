using System.Text.Json.Nodes;
using Xunit;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Execution.Runtime;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Tests.Configuration;

/// <summary>自动更新配置：还原描述解析与执行、增量同步有效性、首次检测时机。</summary>
public class ConfigSwapSyncTests
{
    private static string MakeTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "np-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ConfigSwapSession.ToggleRestore ArrayToggle(string path, Dictionary<string, bool> initial)
        => new() { Type = "array", Path = path, KeyField = "id", EnabledField = "enabled", Initial = initial };

    private static ConfigSwapSession.ToggleRestore MapToggle(string path, Dictionary<string, bool> initial)
        => new() { Type = "map", Path = path, Initial = initial };

    private static ConfigSwapSession.ToggleRestore BoolArrayToggle(string path, List<bool> initial)
        => new() { Type = "boolArray", Path = path, InitialList = initial };

    [Fact]
    public void ShouldRunFirstSyncAndRestoreKindFollowCurrentContract()
    {
        Assert.False(RunSession.ShouldRunFirstSync(14.9, 15));
        Assert.True(RunSession.ShouldRunFirstSync(15, 15));

        Assert.Equal(PathKind.File, ConfigSwapPrimitives.RestoreKind(new ConfigSessionMark { ConfigPath = "C:\\cfg\\state.json", ConfigKind = "missing" }));
        Assert.Equal(PathKind.Dir, ConfigSwapPrimitives.RestoreKind(new ConfigSessionMark { ConfigPath = "C:\\cfg\\config", ConfigKind = "missing" }));
    }

    [Fact]
    public void RestoreConfigReplacements_CorruptMeta_IsQuarantined()
    {
        string scriptId = "kn74-" + Guid.NewGuid().ToString("N");
        string backupDir = ConfigPaths.ReplaceBackupDir(scriptId, "user");
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, ".meta"), "{not-json");
        string parent = Directory.GetParent(backupDir)!.FullName;
        try
        {
            Assert.False(ConfigSwapSession.RestoreConfigReplacements(scriptId, "user"));
            Assert.False(Directory.Exists(backupDir));
            Assert.Single(Directory.GetDirectories(parent, Path.GetFileName(backupDir) + ".corrupt-*"));
        }
        finally
        {
            string dataDir = Path.Combine(AppPaths.DataDir, scriptId);
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void LocateNode_ResolvesArrayIndexAndStableIdSelector()
    {
        JsonNode root = JsonNode.Parse("{\"instances\":[{\"id\":\"first\",\"tasks\":[{\"id\":\"t1\"}]},{\"id\":\"second\",\"tasks\":[{\"id\":\"t2\"}]}]}")!;

        Assert.Single((JsonArray)ConfigSwapSession.LocateNode(root, "instances[0].tasks")!);
        JsonNode? byId = ConfigSwapSession.LocateNode(root, "instances[id=second].tasks");
        Assert.Equal("t2", ((JsonArray)byId!)[0]!["id"]!.ToString());
    }

    [Fact]
    public void LocateNode_InvalidPath_ReturnsNull()
    {
        JsonNode root = JsonNode.Parse("{\"instances\":[{\"tasks\":[]}]}")!;
        Assert.Null(ConfigSwapSession.LocateNode(root, "instances[9].tasks"));
        Assert.Null(ConfigSwapSession.LocateNode(root, "missing.tasks"));
        Assert.Null(ConfigSwapSession.LocateNode(root, "instances[abc].tasks"));
    }

    [Fact]
    public void ApplyToggle_ArrayAndMap_RestoreInitialAndKeepUnlisted()
    {
        string content = "{\"instances\":[{\"tasks\":[{\"id\":\"t1\",\"enabled\":false},{\"id\":\"t2\",\"enabled\":false},{\"id\":\"t3\",\"enabled\":true}]}]}";
        Assert.True(ConfigSwapSession.ApplyToggle(ref content, ArrayToggle("instances[0].tasks", new Dictionary<string, bool> { ["t1"] = true, ["t2"] = true })));
        JsonArray tasks = (JsonArray)JsonNode.Parse(content)!["instances"]![0]!["tasks"]!;
        Assert.True((bool)tasks[0]!["enabled"]!);
        Assert.True((bool)tasks[2]!["enabled"]!);

        string mapContent = "{\"TaskEnabledList\":{\"g1\":false,\"g3\":true}}";
        Assert.True(ConfigSwapSession.ApplyToggle(ref mapContent, MapToggle("TaskEnabledList", new Dictionary<string, bool> { ["g1"] = true })));
        JsonObject map = (JsonObject)JsonNode.Parse(mapContent)!["TaskEnabledList"]!;
        Assert.True((bool)map["g1"]!);
        Assert.True((bool)map["g3"]!);
    }

    [Fact]
    public void ApplyToggle_BoolArrayAndInvalidTargetFailClosed()
    {
        string content = "{\"onoff\":[false,false,false]}";
        Assert.True(ConfigSwapSession.ApplyToggle(ref content, BoolArrayToggle("onoff", new List<bool> { true, false, true })));
        JsonArray onoff = (JsonArray)JsonNode.Parse(content)!["onoff"]!;
        Assert.True((bool)onoff[0]!);
        Assert.False((bool)onoff[1]!);

        string shortTarget = "{\"onoff\":[false]}";
        Assert.False(ConfigSwapSession.ApplyToggle(ref shortTarget, BoolArrayToggle("onoff", new List<bool> { true, true })));

        string missing = "{\"tasks\":[{\"id\":\"t1\",\"enabled\":false}]}";
        Assert.False(ConfigSwapSession.ApplyToggle(ref missing, ArrayToggle("missing.tasks", new Dictionary<string, bool> { ["t1"] = true })));
        string bad = "not-json{";
        Assert.False(ConfigSwapSession.ApplyToggle(ref bad, ArrayToggle("tasks", new Dictionary<string, bool> { ["t1"] = true })));
    }

    [Fact]
    public void ReadRestoreDescriptor_ParsesArrayAndBoolArrayAndRejectsMalformed()
    {
        string dir = MakeTempDir();
        File.WriteAllText(Path.Combine(dir, "config-restore.json"),
            "{\"files\":[{\"file\":\"mxu-MaaEnd.json\",\"toggles\":["
            + "{\"type\":\"array\",\"path\":\"instances[0].tasks\",\"keyField\":\"id\",\"enabledField\":\"enabled\",\"initial\":{\"t1\":true,\"t2\":false}},"
            + "{\"type\":\"boolArray\",\"path\":\"onoff\",\"initial\":[true,false,true]}]}]}");

        ConfigSwapSession.ConfigRestoreDescriptor? descriptor = ConfigSwapSession.ReadRestoreDescriptor(dir);
        Assert.NotNull(descriptor);
        Assert.Equal("mxu-MaaEnd.json", Assert.Single(descriptor!.Files).File);
        Assert.True(descriptor.Files[0].Toggles[0].Initial["t1"]);
        Assert.Equal(new List<bool> { true, false, true }, descriptor.Files[0].Toggles[1].InitialList);

        string badDir = MakeTempDir();
        File.WriteAllText(Path.Combine(badDir, "config-restore.json"), "not-json");
        Assert.Null(ConfigSwapSession.ReadRestoreDescriptor(badDir));
        Assert.Null(ConfigSwapSession.ReadRestoreDescriptor(MakeTempDir()));
    }

    [Fact]
    public void ValidForSync_SkipsEmptyBrokenAndShrinkingSources()
    {
        string emptyFile = Path.Combine(MakeTempDir(), "e.json");
        File.WriteAllText(emptyFile, "");
        Assert.False(ConfigSwapSession.ValidForSync(emptyFile, MakeTempDir()));
        Assert.False(ConfigSwapSession.ValidForSync(Path.Combine(MakeTempDir(), "missing"), MakeTempDir()));

        string broken = Path.Combine(MakeTempDir(), "cfg.json");
        File.WriteAllText(broken, "{\"tasks\":[{\"id\":\"t1\",\"enabled\":true");
        Assert.False(ConfigSwapSession.ValidForSync(broken, MakeTempDir()));

        string valid = Path.Combine(MakeTempDir(), "cfg.json");
        File.WriteAllText(valid, "{\"tasks\":[{\"id\":\"t1\",\"enabled\":true}]}");
        Assert.True(ConfigSwapSession.ValidForSync(valid, MakeTempDir()));

        string cfg = MakeTempDir();
        string store = MakeTempDir();
        File.WriteAllText(Path.Combine(cfg, "a.txt"), "A");
        for (int i = 0; i < 4; i++) File.WriteAllText(Path.Combine(store, "s" + i + ".txt"), "S");
        Assert.False(ConfigSwapSession.ValidForSync(cfg, store));
    }
}
