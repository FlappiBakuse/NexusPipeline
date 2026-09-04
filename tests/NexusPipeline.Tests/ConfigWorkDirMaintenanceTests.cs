using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>当前 work/ 事务工作区的路径钉扎、空闲清扫与运行时暂存区清扫。</summary>
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
        Assert.Equal(Path.Combine(work, "original"), ConfigSwapPaths.CacheDir(scriptId, userName));
        Assert.Equal(Path.Combine(work, "edit-hidden"), ConfigSwapPaths.HiddenConfigDir(scriptId, userName));
        Assert.Equal(Path.Combine(work, "script"), ConfigSwapPaths.ScriptDir(scriptId, userName));
        Assert.Equal(Path.Combine(work, "swap-backup"), ConfigSwapPaths.ReplaceBackupDir(scriptId, userName));
        // 无用户兜底同样收敛到 work/
        Assert.Equal(Path.Combine(ScriptRoot(scriptId), "work", "script"), ConfigSwapPaths.ScriptDir(scriptId, null));
        Assert.Equal(Path.Combine(ScriptRoot(scriptId), "work", "swap-backup"), ConfigSwapPaths.ReplaceBackupDir(scriptId, null));
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
            Directory.CreateDirectory(Path.Combine(idle, "work", "unknown-residue"));
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
