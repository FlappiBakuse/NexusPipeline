using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>配置快照时机与编辑模式：
/// 运行隐式建快照（store 空 + config 存在 → 复制现场配置为初始快照）；
/// 首次编辑配置 fresh/reuse 模式的文件动作矩阵（done=复制入库、fresh cancel=先清生成物再移回原配置、
/// reuse=无文件动作）；当前格式会话标记的恢复。</summary>
public class ConfigEditModesTests
{
    public ConfigEditModesTests()
    {
        // PrepareForRun / 编辑会话准备都会先走 RecoverIfNeeded，测试环境装配空恢复委托（无标记时为 no-op）
        ConfigSwapSession.ConfigureRecovery(_ => null, () => []);
    }

    private static (string ScriptId, string UserName, string ConfigPath) MakeTarget(string tempRoot)
    {
        string scriptId = "editmode-" + Guid.NewGuid().ToString("N");
        // config 路径放在数据目录之外，避免与 store/original 混淆
        string configPath = Path.Combine(tempRoot, scriptId, "config.json");
        return (scriptId, "user", configPath);
    }

    private static string DataRoot(string scriptId)
    {
        return Path.Combine(AppPaths.DataDir, scriptId);
    }

    private static void Cleanup(string scriptId, string tempRoot)
    {
        string dataRoot = DataRoot(scriptId);
        if (Directory.Exists(dataRoot))
        {
            Directory.Delete(dataRoot, recursive: true);
        }
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string ReadFile(string path)
    {
        return File.ReadAllText(path);
    }

    /* ---------------- 运行隐式建快照 ---------------- */

    [Fact]
    public void PrepareForRun_EmptyStore_CopiesConfigAsInitialSnapshot()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "user-current");
        try
        {
            Assert.True(UserConfigManager.PrepareForRun(scriptId, userName, configPath, out string? error), error);
            Assert.Null(error);

            // 初始快照已建立且内容为现场配置
            Assert.True(UserConfigManager.HasSnapshot(scriptId, userName));
            Assert.Equal("user-current", ReadFile(Path.Combine(UserConfigManager.StoreDir(scriptId, userName), "config.json")));
            // 交换语义不变：config 位置是快照副本，original 缓存是原配置
            Assert.Equal("user-current", ReadFile(configPath));
            Assert.Equal("user-current", ReadFile(Path.Combine(UserConfigManager.CacheDir(scriptId, userName), "config.json")));
        }
        finally
        {
            UserConfigManager.RestoreAfterRun(scriptId, userName, configPath);
            Cleanup(scriptId, tempRoot);
        }
    }

    [Fact]
    public void PrepareForRun_MissingConfig_KeepsEmptyStore()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        try
        {
            Assert.True(UserConfigManager.PrepareForRun(scriptId, userName, configPath, out string? error), error);
            Assert.Null(error);
            // Missing config：无快照可建（store 目录保持不存在），运行期间由脚本生成、收尾自动更新配置同步入库
            Assert.False(UserConfigManager.HasSnapshot(scriptId, userName));
            Assert.False(Directory.Exists(UserConfigManager.StoreDir(scriptId, userName)));
        }
        finally
        {
            UserConfigManager.RestoreAfterRun(scriptId, userName, configPath);
            Cleanup(scriptId, tempRoot);
        }
    }

    /* ---------------- fresh（全新配置） ---------------- */

    [Fact]
    public void FreshStart_WithExistingConfig_MovesConfigToCache()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "original");
        try
        {
            Assert.Null(UserConfigManager.PrepareForEditFresh(scriptId, userName, configPath));

            // 原配置移入缓存区，config 位置为空（脚本将在此生成新配置）
            Assert.False(File.Exists(configPath));
            Assert.Equal("original", ReadFile(Path.Combine(UserConfigManager.CacheDir(scriptId, userName), "config.json")));
            ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
            Assert.NotNull(mark);
            Assert.Equal("fresh", mark!.EditMode);
            Assert.Equal("edit", mark.SessionPhase);
            Assert.False(UserConfigManager.HasSnapshot(scriptId, userName));

            // done：新配置复制入库 + 原配置移回 config 位置
            File.WriteAllText(configPath, "generated");
            Assert.Null(UserConfigManager.CommitEdit(scriptId, userName, configPath));
            Assert.Equal("generated", ReadFile(Path.Combine(UserConfigManager.StoreDir(scriptId, userName), "config.json")));
            Assert.Equal("original", ReadFile(configPath));
            Assert.Null(ConfigSessionMark.TryRead(scriptId, userName));
        }
        finally
        {
            Cleanup(scriptId, tempRoot);
        }
    }

    [Fact]
    public void FreshCancel_RemovesGenerated_AndRestoresOriginal()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "original");
        try
        {
            Assert.Null(UserConfigManager.PrepareForEditFresh(scriptId, userName, configPath));
            File.WriteAllText(configPath, "generated");

            Assert.Null(UserConfigManager.CancelEdit(scriptId, userName, configPath));

            // 先清生成物，再移回原配置
            Assert.Equal("original", ReadFile(configPath));
            Assert.False(Directory.Exists(UserConfigManager.StoreDir(scriptId, userName)));
            Assert.Null(ConfigSessionMark.TryRead(scriptId, userName));
        }
        finally
        {
            Cleanup(scriptId, tempRoot);
        }
    }

    [Fact]
    public void FreshCancel_WhenConfigWasMissing_RestoresMissing()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        try
        {
            Assert.Null(UserConfigManager.PrepareForEditFresh(scriptId, userName, configPath));
            Directory.CreateDirectory(configPath);
            File.WriteAllText(Path.Combine(configPath, "generated.json"), "generated");

            Assert.Null(UserConfigManager.CancelEdit(scriptId, userName, configPath));

            Assert.False(File.Exists(Path.Combine(configPath, "generated.json")));
            Assert.Equal(PathKind.Missing, PathKindUtil.KindOf(configPath));
            Assert.Null(ConfigSessionMark.TryRead(scriptId, userName));
        }
        finally
        {
            Cleanup(scriptId, tempRoot);
        }
    }

    [Fact]
    public void FreshWithExtra_FirstEditCommitCreatesExtraSnapshot_AndRestoresOriginalSite()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        string extraPath = Path.Combine(tempRoot, scriptId, "User", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(extraPath)!);
        File.WriteAllText(configPath, "main-original");
        File.WriteAllText(extraPath, "{\"value\":\"extra-original\"}");
        try
        {
            Assert.Null(UserConfigManager.PrepareForEditFresh(
                scriptId,
                userName,
                configPath,
                extraConfigPaths: [extraPath]));

            string extraStore = ConfigSwapPaths.StoreExtraDir(scriptId, userName, extraPath);
            Assert.False(Directory.Exists(extraStore));
            Assert.Equal("{\"value\":\"extra-original\"}", ReadFile(extraPath));

            File.WriteAllText(configPath, "main-generated");
            File.WriteAllText(extraPath, "{\"value\":\"extra-edited\"}");
            Assert.Null(UserConfigManager.CommitEdit(scriptId, userName, configPath));

            Assert.Equal("main-generated", ReadFile(Path.Combine(UserConfigManager.StoreDir(scriptId, userName), "config.json")));
            Assert.Equal("{\"value\":\"extra-edited\"}", ReadFile(Path.Combine(extraStore, "config.json")));
            Assert.Equal("main-original", ReadFile(configPath));
            Assert.Equal("{\"value\":\"extra-original\"}", ReadFile(extraPath));
            Assert.Null(ConfigSessionMark.TryRead(scriptId, userName));
        }
        finally
        {
            Cleanup(scriptId, tempRoot);
        }
    }

    [Fact]
    public void FreshWithExtra_CancelDoesNotCreateExtraSnapshot()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        string extraPath = Path.Combine(tempRoot, scriptId, "User", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(extraPath)!);
        File.WriteAllText(configPath, "main-original");
        File.WriteAllText(extraPath, "{\"value\":\"extra-original\"}");
        try
        {
            Assert.Null(UserConfigManager.PrepareForEditFresh(
                scriptId,
                userName,
                configPath,
                extraConfigPaths: [extraPath]));
            File.WriteAllText(configPath, "main-generated");
            File.WriteAllText(extraPath, "{\"value\":\"extra-edited\"}");

            Assert.Null(UserConfigManager.CancelEdit(scriptId, userName, configPath));

            Assert.False(Directory.Exists(ConfigSwapPaths.StoreExtraDir(scriptId, userName, extraPath)));
            Assert.Equal("main-original", ReadFile(configPath));
            Assert.Equal("{\"value\":\"extra-original\"}", ReadFile(extraPath));
            Assert.Null(ConfigSessionMark.TryRead(scriptId, userName));
        }
        finally
        {
            Cleanup(scriptId, tempRoot);
        }
    }

    /* ---------------- reuse（复用配置） ---------------- */

    [Fact]
    public void ReuseStart_NoFileAction()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "user-content");
        try
        {
            Assert.Null(UserConfigManager.PrepareForEditReuse(scriptId, userName, configPath));

            // 无任何文件动作：config 原位原内容，无缓存区、无快照
            Assert.Equal("user-content", ReadFile(configPath));
            Assert.False(Directory.Exists(UserConfigManager.CacheDir(scriptId, userName)));
            Assert.False(UserConfigManager.HasSnapshot(scriptId, userName));
            ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
            Assert.NotNull(mark);
            Assert.Equal("reuse", mark!.EditMode);
        }
        finally
        {
            UserConfigManager.CancelEdit(scriptId, userName, configPath);
            Cleanup(scriptId, tempRoot);
        }
    }

    [Fact]
    public void ReuseDone_CopiesConfigToStore_AndKeepsConfigInPlace()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "user-content");
        try
        {
            Assert.Null(UserConfigManager.PrepareForEditReuse(scriptId, userName, configPath));
            File.WriteAllText(configPath, "edited");

            Assert.Null(UserConfigManager.CommitEdit(scriptId, userName, configPath));

            // 编辑结果复制入库，config 原位保持编辑后内容
            Assert.Equal("edited", ReadFile(Path.Combine(UserConfigManager.StoreDir(scriptId, userName), "config.json")));
            Assert.Equal("edited", ReadFile(configPath));
            Assert.Null(ConfigSessionMark.TryRead(scriptId, userName));
        }
        finally
        {
            Cleanup(scriptId, tempRoot);
        }
    }

    [Fact]
    public void ReuseCancel_HasNoFileAction()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "user-content");
        try
        {
            Assert.Null(UserConfigManager.PrepareForEditReuse(scriptId, userName, configPath));

            Assert.Null(UserConfigManager.CancelEdit(scriptId, userName, configPath));

            Assert.Equal("user-content", ReadFile(configPath));
            Assert.False(UserConfigManager.HasSnapshot(scriptId, userName));
            Assert.Null(ConfigSessionMark.TryRead(scriptId, userName));
        }
        finally
        {
            Cleanup(scriptId, tempRoot);
        }
    }

    [Fact]
    public void ReuseCancel_ClearsPendingInputWithoutCreatingSnapshot()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "user-content");
        var options = new ConfigEditPreparationOptions(
            false,
            Array.Empty<string>(),
            new ConfigEditPendingInput { Name = "instance", Value = "02" });
        try
        {
            Assert.Null(UserConfigManager.PrepareForEditReuse(
                scriptId,
                userName,
                configPath,
                options: options));

            ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
            Assert.NotNull(mark);
            Assert.Equal("instance", mark!.PendingConfigInput!.Name);
            Assert.Equal("02", mark.PendingConfigInput.Value);

            Assert.Null(UserConfigManager.CancelEdit(scriptId, userName, configPath));

            Assert.False(UserConfigManager.HasSnapshot(scriptId, userName));
            Assert.Null(ConfigSessionMark.TryRead(scriptId, userName));
        }
        finally
        {
            Cleanup(scriptId, tempRoot);
        }
    }

    [Fact]
    public void ReuseCommit_WithPendingInputKeepsCommitMarkerUntilBindingCommit()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "user-content");
        var options = new ConfigEditPreparationOptions(
            false,
            Array.Empty<string>(),
            new ConfigEditPendingInput { Name = "instance", Value = "02" });
        try
        {
            Assert.Null(UserConfigManager.PrepareForEditReuse(
                scriptId,
                userName,
                configPath,
                options: options));
            File.WriteAllText(configPath, "edited");

            Assert.Null(UserConfigManager.CommitEdit(scriptId, userName, configPath));

            ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
            Assert.NotNull(mark);
            Assert.Equal("edit-commit-pending", mark!.SessionPhase);
            Assert.Equal("instance", mark.PendingConfigInput!.Name);
            Assert.Equal("02", mark.PendingConfigInput.Value);
            Assert.Equal("edited", ReadFile(Path.Combine(UserConfigManager.StoreDir(scriptId, userName), "config.json")));
        }
        finally
        {
            ConfigSessionMark.Clear(scriptId, userName);
            Cleanup(scriptId, tempRoot);
        }
    }

    /* ---------------- normal（编辑既有快照） ---------------- */

    [Fact]
    public void NormalStart_MaterializesSnapshot_AndCommitWritesDiffBack()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        try
        {
            // 先建立权威快照（运行隐式建快照语义），再进入 normal 编辑
            File.WriteAllText(configPath, "user-current");
            Assert.True(UserConfigManager.PrepareForRun(scriptId, userName, configPath, out string? runError), runError);
            Assert.True(UserConfigManager.HasSnapshot(scriptId, userName));

            Assert.Null(UserConfigManager.PrepareForEdit(scriptId, userName, configPath));
            File.WriteAllText(configPath, "edited-in-target");

            Assert.Null(UserConfigManager.CommitEdit(scriptId, userName, configPath));
            Assert.Equal("edited-in-target", ReadFile(Path.Combine(UserConfigManager.StoreDir(scriptId, userName), "config.json")));
            Assert.Null(ConfigSessionMark.TryRead(scriptId, userName));
        }
        finally
        {
            Cleanup(scriptId, tempRoot);
        }
    }

    [Fact]
    public void NormalCommit_WhenConfigRenamedAway_FailsWithMissingLocation()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        try
        {
            File.WriteAllText(configPath, "user-current");
            Assert.True(UserConfigManager.PrepareForRun(scriptId, userName, configPath, out string? runError), runError);
            Assert.Null(UserConfigManager.PrepareForEdit(scriptId, userName, configPath));

            // 模拟目标软件中改名：编辑目标文件消失（内容随新名字出现在别处）
            File.Delete(configPath);

            string? commitError = UserConfigManager.CommitEdit(scriptId, userName, configPath);
            Assert.NotNull(commitError);
            Assert.Contains("配置位置不存在", commitError);
            // 提交被拒绝后现场保留（上层保留会话供用户恢复文件或取消），store 快照未被污染
            Assert.True(UserConfigManager.HasSnapshot(scriptId, userName));
            Assert.NotNull(ConfigSessionMark.TryRead(scriptId, userName));
        }
        finally
        {
            Cleanup(scriptId, tempRoot);
        }
    }

    [Fact]
    public void NormalCancel_WhenConfigRenamedAway_RestoresOriginal()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        try
        {
            File.WriteAllText(configPath, "user-current");
            Assert.True(UserConfigManager.PrepareForRun(scriptId, userName, configPath, out string? runError), runError);
            Assert.Null(UserConfigManager.PrepareForEdit(scriptId, userName, configPath));
            File.Delete(configPath);

            // 目标文件被改名后取消编辑：编辑前原文件必须还原回原位置，会话干净结束
            Assert.Null(UserConfigManager.CancelEdit(scriptId, userName, configPath));
            Assert.Equal("user-current", ReadFile(configPath));
            Assert.Null(ConfigSessionMark.TryRead(scriptId, userName));
        }
        finally
        {
            Cleanup(scriptId, tempRoot);
        }
    }

    [Fact]
    public void NormalMultiCandidateIsolation_MovesSiblings_AndRestoresThemOnCommit()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        string configDir = Path.GetDirectoryName(configPath)!;
        string siblingA = Path.Combine(configDir, "A.json");
        string siblingC = Path.Combine(configDir, "C.json");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(configPath, "target-original");
        File.WriteAllText(siblingA, "a-original");
        File.WriteAllText(siblingC, "c-original");
        try
        {
            Assert.True(UserConfigManager.PrepareForRun(scriptId, userName, configPath, out string? runError), runError);
            Assert.Null(UserConfigManager.RestoreAfterRun(scriptId, userName, configPath));

            var options = new ConfigEditPreparationOptions(true, [siblingA, configPath, siblingC]);
            Assert.Null(UserConfigManager.PrepareForEdit(
                scriptId,
                userName,
                configPath,
                extraConfigPaths: null,
                options: options));

            Assert.Equal("target-original", ReadFile(configPath));
            Assert.False(File.Exists(siblingA));
            Assert.False(File.Exists(siblingC));
            ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
            Assert.NotNull(mark);
            Assert.Equal(2, mark!.EditIsolationPaths.Count);
            Assert.True(Directory.Exists(UserConfigManager.EditIsolationDir(scriptId, userName)));

            File.WriteAllText(configPath, "target-edited");
            Assert.Null(UserConfigManager.CommitEdit(scriptId, userName, configPath));

            Assert.Equal("target-edited", ReadFile(Path.Combine(UserConfigManager.StoreDir(scriptId, userName), "config.json")));
            Assert.Equal("target-original", ReadFile(configPath));
            Assert.Equal("a-original", ReadFile(siblingA));
            Assert.Equal("c-original", ReadFile(siblingC));
            Assert.False(Directory.Exists(UserConfigManager.EditIsolationDir(scriptId, userName)));
            Assert.Null(ConfigSessionMark.TryRead(scriptId, userName));
        }
        finally
        {
            Cleanup(scriptId, tempRoot);
        }
    }

    [Fact]
    public void ReuseMultiDirectoryIsolation_CopiesSelectedWorking_AndRestoresAllDirectories()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        string scriptId = "editmode-" + Guid.NewGuid().ToString("N");
        const string userName = "user";
        string configDir = Path.Combine(tempRoot, scriptId, "config");
        string selected = Path.Combine(configDir, "02");
        string first = Path.Combine(configDir, "01");
        string third = Path.Combine(configDir, "03");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(third);
        File.WriteAllText(Path.Combine(first, "value.txt"), "one-original");
        File.WriteAllText(Path.Combine(selected, "value.txt"), "two-original");
        File.WriteAllText(Path.Combine(third, "value.txt"), "three-original");
        try
        {
            var options = new ConfigEditPreparationOptions(true, [first, selected, third]);
            Assert.Null(UserConfigManager.PrepareForEditReuse(
                scriptId,
                userName,
                selected,
                options: options));

            Assert.False(Directory.Exists(first));
            Assert.Equal("two-original", ReadFile(Path.Combine(selected, "value.txt")));
            Assert.False(Directory.Exists(third));
            Assert.Equal(3, ConfigSessionMark.TryRead(scriptId, userName)!.EditIsolationPaths.Count);

            File.WriteAllText(Path.Combine(selected, "value.txt"), "two-edited");
            Assert.Null(UserConfigManager.CommitEdit(scriptId, userName, selected));

            Assert.Equal("two-edited", ReadFile(Path.Combine(UserConfigManager.StoreDir(scriptId, userName), "value.txt")));
            Assert.Equal("one-original", ReadFile(Path.Combine(first, "value.txt")));
            Assert.Equal("two-original", ReadFile(Path.Combine(selected, "value.txt")));
            Assert.Equal("three-original", ReadFile(Path.Combine(third, "value.txt")));
            Assert.False(Directory.Exists(UserConfigManager.EditIsolationDir(scriptId, userName)));
            Assert.Null(ConfigSessionMark.TryRead(scriptId, userName));
        }
        finally
        {
            Cleanup(scriptId, tempRoot);
        }
    }

    /* ---------------- 会话标记格式与恢复 ---------------- */

    [Fact]
    public void SessionMarkMissingRequiredExtraPaths_IsPreservedWithoutGuessing()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "new-value");
        try
        {
            // 手工构造缺少当前必需字段的会话现场：不参与恢复，现场保持原样供人工处理。
            string cache = UserConfigManager.CacheDir(scriptId, userName);
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "config.json"), "original");
            string markJson = """
                {
                  "ScriptId": "%SCRIPT%",
                  "UserId": "user",
                  "ConfigPath": "%CONFIG%",
                  "SessionPhase": "edit",
                  "ConfigKind": "file",
                  "WorkingDirectory": "",
                  "LaunchExe": "",
                  "ProcessIdentity": "",
                  "ProfileHash": "",
                  "PluginName": "",
                  "PluginVersion": "",
                  "EditMode": "normal",
                  "StartedAt": "2026-09-01T08:00:00",
                  "UnsupportedField": true
                }
                """.Replace("%SCRIPT%", scriptId).Replace("%CONFIG%", configPath.Replace("\\", "\\\\"));
            File.WriteAllText(ConfigSessionMark.MarkFile(scriptId, userName), markJson);

            ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
            Assert.Null(mark);
            Assert.True(File.Exists(ConfigSessionMark.MarkFile(scriptId, userName)));
            Assert.True(File.Exists(Path.Combine(cache, "config.json")));
            Assert.Equal("new-value", ReadFile(configPath));
        }
        finally
        {
            Cleanup(scriptId, tempRoot);
        }
    }

    [Fact]
    public void CurrentFreshMissingMark_RecoveryClearsGeneratedConfig()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        try
        {
            // fresh + 原配置 Missing 的崩溃现场：缓存区为空，config 位置留有脚本生成物
            Directory.CreateDirectory(configPath);
            File.WriteAllText(Path.Combine(configPath, "generated.json"), "generated");
            var mark = new ConfigSessionMark
            {
                ScriptId = scriptId,
                UserId = userName,
                ConfigPath = configPath,
                ConfigKind = "missing",
                SessionPhase = "edit",
                EditMode = "fresh",
            };
            mark.Write();
            Assert.True(mark.NeedsFreshRestore);

            // 用户级会话标记按当前全局用户绑定白名单恢复
            var user = new NexusUser
            {
                Id = userName,
                Bindings = [new UserScriptBinding { ScriptInstanceId = scriptId }],
            };
            UserConfigManager.RecoverInterrupted([user]);

            Assert.False(File.Exists(Path.Combine(configPath, "generated.json")));
            Assert.Null(ConfigSessionMark.TryRead(scriptId, userName));
        }
        finally
        {
            Cleanup(scriptId, tempRoot);
        }
    }

    [Fact]
    public void NeedsFreshRestore_TrueOnlyForFreshWithMissingOriginal()
    {
        Assert.True(new ConfigSessionMark { EditMode = "fresh", ConfigKind = "missing" }.NeedsFreshRestore);
        Assert.False(new ConfigSessionMark { EditMode = "fresh", ConfigKind = "file" }.NeedsFreshRestore);
        Assert.False(new ConfigSessionMark { EditMode = "reuse", ConfigKind = "missing" }.NeedsFreshRestore);
        Assert.False(new ConfigSessionMark { ConfigKind = "missing" }.NeedsFreshRestore);
    }

    [Fact]
    public void HasSnapshot_TrueOnlyWhenStoreHasContent()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "np-editmode-" + Guid.NewGuid().ToString("N"));
        var (scriptId, userName, configPath) = MakeTarget(tempRoot);
        try
        {
            Assert.False(UserConfigManager.HasSnapshot(scriptId, userName));
            Directory.CreateDirectory(UserConfigManager.StoreDir(scriptId, userName));
            Assert.False(UserConfigManager.HasSnapshot(scriptId, userName));
            File.WriteAllText(Path.Combine(UserConfigManager.StoreDir(scriptId, userName), "config.json"), "{}");
            Assert.True(UserConfigManager.HasSnapshot(scriptId, userName));
        }
        finally
        {
            Cleanup(scriptId, tempRoot);
        }
    }
}
