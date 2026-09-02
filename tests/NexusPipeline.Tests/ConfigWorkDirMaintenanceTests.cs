using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>v0.13.2 work/ 事务工作区治理：旧布局一次性迁移（幂等、保留现场、dot 后缀改名）、
/// store 同步事务恢复（work/store-tmp + store-previous）、空闲清扫（无标记且无残留时整体消失）、
/// 以及 data/{脚本Id}/{UserId} 新布局的路径钉扎。</summary>
public class ConfigWorkDirMaintenanceTests
{
    public ConfigWorkDirMaintenanceTests()
    {
        ConfigSwapSession.ConfigureRecovery(_ => null, () => []);
    }

    private static string ScriptRoot(string scriptId) => Path.Combine(AppPaths.DataDir, scriptId);

    private static string UserRoot(string scriptId, string userName) => Path.Combine(ScriptRoot(scriptId), userName);

    private static void CleanupScript(string scriptId)
    {
        string root = ScriptRoot(scriptId);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /* ---------------- 布局钉扎 ---------------- */

    [Fact]
    public void ConfigSwapPaths_WorkLayout_PinsConsolidatedPaths()
    {
        const string scriptId = "layout-pins";
        const string userName = "user-a";
        string userRoot = UserRoot(scriptId, userName);
        string work = Path.Combine(userRoot, "work");

        Assert.Equal(Path.Combine(userRoot, "store"), ConfigSwapPaths.StoreDir(scriptId, userName));
        Assert.Equal(Path.Combine(userRoot, "store-meta.json"), ConfigSwapPaths.StoreMetadataPath(scriptId, userName));
        Assert.Equal(Path.Combine(userRoot, "store-archive"), ConfigSwapPaths.StoreArchiveDir(scriptId, userName));
        Assert.Equal(Path.Combine(userRoot, "store-previous"), ConfigSwapPaths.StorePreviousDir(scriptId, userName));
        Assert.Equal(Path.Combine(work, "original"), ConfigSwapPaths.CacheDir(scriptId, userName));
        Assert.Equal(Path.Combine(work, "store-tmp"), ConfigSwapPaths.StoreTempDir(scriptId, userName));
        Assert.Equal(Path.Combine(work, "retry-store"), ConfigSwapPaths.RetryStoreDir(scriptId, userName));
        Assert.Equal(Path.Combine(work, "edit-hidden"), ConfigSwapPaths.HiddenConfigDir(scriptId, userName));
        Assert.Equal(Path.Combine(work, "script"), ConfigSwapPaths.ScriptDir(scriptId, userName));
        Assert.Equal(Path.Combine(work, "swap-backup"), ConfigSwapPaths.ReplaceBackupDir(scriptId, userName));
        // 无用户兜底同样收敛到 work/
        Assert.Equal(Path.Combine(ScriptRoot(scriptId), "work", "script"), ConfigSwapPaths.ScriptDir(scriptId, null));
        Assert.Equal(Path.Combine(ScriptRoot(scriptId), "work", "swap-backup"), ConfigSwapPaths.ReplaceBackupDir(scriptId, null));
    }

    /* ---------------- 一次性迁移 ---------------- */

    [Fact]
    public void MigrateLegacyWorkDirs_MovesLegacyDirsIntoWork_AndRenamesDotSuffixEntries()
    {
        const string scriptId = "migrate-basic";
        string userRoot = UserRoot(scriptId, "user-a");
        try
        {
            foreach (KeyValuePair<string, string> legacy in ConfigSwapPaths.LegacyWorkItemMap)
            {
                string dir = Path.Combine(userRoot, legacy.Key);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "marker.txt"), legacy.Key);
            }
            Directory.CreateDirectory(Path.Combine(userRoot, "store"));
            File.WriteAllText(Path.Combine(userRoot, "store", "config.json"), "snapshot");
            File.WriteAllText(Path.Combine(userRoot, "store.meta.json"), "{}");
            Directory.CreateDirectory(Path.Combine(userRoot, "store.previous"));
            File.WriteAllText(Path.Combine(userRoot, "store.previous", "config.json"), "old");
            File.WriteAllText(Path.Combine(userRoot, ".session"), "{}");

            ConfigWorkDirMaintenance.MigrateLegacyWorkDirs();

            // 事务目录按映射迁入 work/（store.tmp → store-tmp 同步改名）
            foreach (KeyValuePair<string, string> legacy in ConfigSwapPaths.LegacyWorkItemMap)
            {
                Assert.False(Directory.Exists(Path.Combine(userRoot, legacy.Key)), legacy.Key);
                Assert.Equal(legacy.Key, File.ReadAllText(Path.Combine(userRoot, "work", legacy.Value, "marker.txt")));
            }
            // 持久层旧 dot 后缀命名原地改名为 kebab-case，内容不变
            Assert.False(File.Exists(Path.Combine(userRoot, "store.meta.json")));
            Assert.False(Directory.Exists(Path.Combine(userRoot, "store.previous")));
            Assert.Equal("{}", File.ReadAllText(Path.Combine(userRoot, "store-meta.json")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(userRoot, "store-previous", "config.json")));
            // store 与会话标记不迁移不改名
            Assert.Equal("snapshot", File.ReadAllText(Path.Combine(userRoot, "store", "config.json")));
            Assert.True(File.Exists(Path.Combine(userRoot, ".session")));

            // 幂等：重复执行无变化
            ConfigWorkDirMaintenance.MigrateLegacyWorkDirs();
            Assert.Equal("original", File.ReadAllText(Path.Combine(userRoot, "work", "original", "marker.txt")));
            Assert.Equal("{}", File.ReadAllText(Path.Combine(userRoot, "store-meta.json")));
        }
        finally
        {
            CleanupScript(scriptId);
        }
    }

    [Fact]
    public void MigrateLegacyWorkDirs_RenamesStaleStoreTmpInsideWork()
    {
        const string scriptId = "migrate-work-tmp";
        string userRoot = UserRoot(scriptId, "user-a");
        try
        {
            // 开发期中间布局：store.tmp 已迁入 work/ 但仍是旧名
            Directory.CreateDirectory(Path.Combine(userRoot, "work", "store.tmp"));
            File.WriteAllText(Path.Combine(userRoot, "work", "store.tmp", "config.json"), "staged");

            ConfigWorkDirMaintenance.MigrateLegacyWorkDirs();

            Assert.False(Directory.Exists(Path.Combine(userRoot, "work", "store.tmp")));
            Assert.Equal("staged", File.ReadAllText(Path.Combine(userRoot, "work", "store-tmp", "config.json")));
        }
        finally
        {
            CleanupScript(scriptId);
        }
    }

    [Fact]
    public void MigrateLegacyWorkDirs_MigratesScriptLevelFallbacks_AndSkipsConflict()
    {
        const string scriptId = "migrate-script-level";
        string scriptRoot = ScriptRoot(scriptId);
        try
        {
            Directory.CreateDirectory(Path.Combine(scriptRoot, "script"));
            File.WriteAllText(Path.Combine(scriptRoot, "script", "probe.js"), "1");
            Directory.CreateDirectory(Path.Combine(scriptRoot, "swap-backup"));
            File.WriteAllText(Path.Combine(scriptRoot, "swap-backup", "cfg.json"), "1");
            // 冲突现场：work/script 已存在
            Directory.CreateDirectory(Path.Combine(scriptRoot, "work", "script"));
            File.WriteAllText(Path.Combine(scriptRoot, "work", "script", "probe.js"), "2");

            ConfigWorkDirMaintenance.MigrateLegacyWorkDirs();

            Assert.Equal("1", File.ReadAllText(Path.Combine(scriptRoot, "work", "swap-backup", "cfg.json")));
            Assert.False(Directory.Exists(Path.Combine(scriptRoot, "swap-backup")));
            // 冲突目录保留双方原样（不做覆盖，交由恢复逻辑与人工核查）
            Assert.Equal("2", File.ReadAllText(Path.Combine(scriptRoot, "work", "script", "probe.js")));
            Assert.Equal("1", File.ReadAllText(Path.Combine(scriptRoot, "script", "probe.js")));
        }
        finally
        {
            CleanupScript(scriptId);
        }
    }

    /* ---------------- store 同步事务恢复（work/store-tmp） ---------------- */

    [Fact]
    public void RecoverInterrupted_PromotesPreviousSnapshot_FromWorkTemp()
    {
        const string scriptId = "recover-storetxn";
        const string userName = "user-a";
        string userRoot = UserRoot(scriptId, userName);
        try
        {
            Directory.CreateDirectory(ConfigSwapPaths.StoreTempDir(scriptId, userName));
            File.WriteAllText(Path.Combine(ConfigSwapPaths.StoreTempDir(scriptId, userName), "config.json"), "staged");
            Directory.CreateDirectory(ConfigSwapPaths.StorePreviousDir(scriptId, userName));
            File.WriteAllText(Path.Combine(ConfigSwapPaths.StorePreviousDir(scriptId, userName), "config.json"), "last-good");

            var user = new NexusUser
            {
                Id = userName,
                Bindings = [new UserScriptBinding { ScriptInstanceId = scriptId }],
            };
            UserConfigManager.RecoverInterrupted([user]);

            // store 缺失 → store-previous 提升；work/store-tmp 清理；work/ 空后整体消失
            Assert.Equal("last-good", File.ReadAllText(Path.Combine(ConfigSwapPaths.StoreDir(scriptId, userName), "config.json")));
            Assert.False(Directory.Exists(ConfigSwapPaths.StoreTempDir(scriptId, userName)));
            Assert.False(Directory.Exists(ConfigSwapPaths.WorkDir(scriptId, userName)));
        }
        finally
        {
            CleanupScript(scriptId);
        }
    }

    [Fact]
    public void RecoverInterrupted_StorePresent_KeepsSnapshotAndDropsWorkTemp()
    {
        const string scriptId = "recover-storetxn-keep";
        const string userName = "user-a";
        string userRoot = UserRoot(scriptId, userName);
        try
        {
            Directory.CreateDirectory(ConfigSwapPaths.StoreDir(scriptId, userName));
            File.WriteAllText(Path.Combine(ConfigSwapPaths.StoreDir(scriptId, userName), "config.json"), "current");
            Directory.CreateDirectory(ConfigSwapPaths.StorePreviousDir(scriptId, userName));
            File.WriteAllText(Path.Combine(ConfigSwapPaths.StorePreviousDir(scriptId, userName), "config.json"), "last-good");
            Directory.CreateDirectory(ConfigSwapPaths.StoreTempDir(scriptId, userName));
            File.WriteAllText(Path.Combine(ConfigSwapPaths.StoreTempDir(scriptId, userName), "config.json"), "staged");

            var user = new NexusUser
            {
                Id = userName,
                Bindings = [new UserScriptBinding { ScriptInstanceId = scriptId }],
            };
            UserConfigManager.RecoverInterrupted([user]);

            Assert.Equal("current", File.ReadAllText(Path.Combine(ConfigSwapPaths.StoreDir(scriptId, userName), "config.json")));
            // 当前 store 继续作为权威快照；旧 store-previous 仍保留，等待一次新协议成功操作后清理。
            Assert.True(Directory.Exists(ConfigSwapPaths.StorePreviousDir(scriptId, userName)));
            Assert.False(Directory.Exists(ConfigSwapPaths.WorkDir(scriptId, userName)));
        }
        finally
        {
            CleanupScript(scriptId);
        }
    }

    /* ---------------- 空闲清扫 ---------------- */

    [Fact]
    public void SweepIdleWorkDirs_RemovesIdleWorkspace_ButKeepsResidueAndActiveSessions()
    {
        const string scriptId = "sweep-idle";
        try
        {
            // 空闲现场：仅剩空子目录与可丢弃子项 → work/ 整体消失
            string idle = UserRoot(scriptId, "idle");
            Directory.CreateDirectory(Path.Combine(idle, "work", "script"));
            File.WriteAllText(Path.Combine(idle, "work", "script", "probe.js"), "1");
            Directory.CreateDirectory(Path.Combine(idle, "work", "retry-store"));
            Directory.CreateDirectory(Path.Combine(idle, "work", "edit-hidden"));

            // 残留现场：swap-backup 有内容 → 保留
            string residue = UserRoot(scriptId, "residue");
            Directory.CreateDirectory(Path.Combine(residue, "work", "script"));
            Directory.CreateDirectory(Path.Combine(residue, "work", "swap-backup"));
            File.WriteAllText(Path.Combine(residue, "work", "swap-backup", "cfg.json"), "original");

            // 活动会话：有 .session 标记 → 完整保留
            string active = UserRoot(scriptId, "active");
            Directory.CreateDirectory(Path.Combine(active, "work", "script"));
            File.WriteAllText(Path.Combine(active, "work", "script", "probe.js"), "1");
            File.WriteAllText(Path.Combine(active, ".session"), "{}");

            ConfigWorkDirMaintenance.SweepIdleWorkDirs();

            Assert.False(Directory.Exists(Path.Combine(idle, "work")));
            Assert.False(Directory.Exists(Path.Combine(residue, "work", "script")));
            Assert.Equal("original", File.ReadAllText(Path.Combine(residue, "work", "swap-backup", "cfg.json")));
            Assert.Equal("1", File.ReadAllText(Path.Combine(active, "work", "script", "probe.js")));
            Assert.True(File.Exists(Path.Combine(active, ".session")));
        }
        finally
        {
            CleanupScript(scriptId);
        }
    }

    /* ---------------- runtime staging 清扫 ---------------- */

    [Fact]
    public void SweepRuntimeStaging_RemovesLeftoverUploads()
    {
        Directory.CreateDirectory(AppPaths.AppearanceStagingDir);
        string leftover = Path.Combine(AppPaths.AppearanceStagingDir, "upload.abc.tmp");
        File.WriteAllText(leftover, "partial");

        ConfigWorkDirMaintenance.SweepRuntimeStaging();

        Assert.False(Directory.Exists(AppPaths.AppearanceStagingDir));
    }
}
