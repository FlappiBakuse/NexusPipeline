using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Services.Configuration;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>
/// 附加配置路径（extraConfigPaths）的幂等同步契约：adopt/快照覆盖/现场备份还原/差异入库，
/// 文件型与目录型附加路径各覆盖一轮；目录键按声明路径稳定定位。
/// </summary>
public class ExtraConfigSyncTests
{
    private static string MakeScriptId()
    {
        return "extra-" + Guid.NewGuid().ToString("N");
    }

    [Fact]
    public void PrepareAdoptsSiteIntoEmptyStoreThenCoversSiteFromStore()
    {
        string scriptId = MakeScriptId();
        string userKey = "user-a";
        string site = Path.Combine(AppPaths.DataDir, scriptId, "site", "software_config.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(site)!);
            File.WriteAllText(site, "原软件配置");

            ExtraConfigSync.PrepareAll(scriptId, userKey, [site]);

            string storeExtra = ConfigSwapPaths.StoreExtraDir(scriptId, userKey, site);
            // 首次 adopt：快照等于现场
            Assert.Equal("原软件配置", File.ReadAllText(Path.Combine(storeExtra, "software_config.json")));
            // 快照非空 → 现场被快照覆盖（内容一致），original-extra 备份现场原文件
            Assert.Equal("原软件配置", File.ReadAllText(site));
            string originalExtra = ConfigSwapPaths.OriginalExtraDir(scriptId, userKey, site);
            Assert.Equal("原软件配置", File.ReadAllText(Path.Combine(originalExtra, "software_config.json")));

            // 快照先行变更 → 再次 Prepare 时快照覆盖现场
            File.WriteAllText(Path.Combine(storeExtra, "software_config.json"), "快照新值");
            ExtraConfigSync.PrepareAll(scriptId, userKey, [site]);
            Assert.Equal("快照新值", File.ReadAllText(site));
        }
        finally
        {
            Cleanup(scriptId);
        }
    }

    [Fact]
    public void RestorePutsSiteBackupBackAndClearsOriginal()
    {
        string scriptId = MakeScriptId();
        string userKey = "user-a";
        string site = Path.Combine(AppPaths.DataDir, scriptId, "site", "software_config.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(site)!);
            File.WriteAllText(site, "运行前现场");

            ExtraConfigSync.PrepareAll(scriptId, userKey, [site]);
            // 模拟脚本运行期间改写了现场
            File.WriteAllText(site, "运行产物");
            ExtraConfigSync.RestoreAll(scriptId, userKey, [site]);

            Assert.Equal("运行前现场", File.ReadAllText(site));
            Assert.False(Directory.Exists(ConfigSwapPaths.OriginalExtraDir(scriptId, userKey, site)));
        }
        finally
        {
            Cleanup(scriptId);
        }
    }

    [Fact]
    public void SyncWritesSiteDifferencesIntoStoreIncludingDeletes()
    {
        string scriptId = MakeScriptId();
        string userKey = "user-a";
        string siteDir = Path.Combine(AppPaths.DataDir, scriptId, "site", "configs");
        try
        {
            Directory.CreateDirectory(siteDir);
            File.WriteAllText(Path.Combine(siteDir, "a.json"), "{\"v\":1}");

            ExtraConfigSync.PrepareAll(scriptId, userKey, [siteDir]);
            string storeExtra = ConfigSwapPaths.StoreExtraDir(scriptId, userKey, siteDir);
            Assert.True(File.Exists(Path.Combine(storeExtra, "a.json")));

            // 现场变更 + 新增 + 删除 → 差异入库
            File.WriteAllText(Path.Combine(siteDir, "a.json"), "{\"v\":2}");
            File.WriteAllText(Path.Combine(siteDir, "b.json"), "{\"v\":1}");
            File.Delete(Path.Combine(siteDir, "a.json"));
            ExtraConfigSync.SyncAllFromSite(scriptId, userKey, [siteDir], "测试同步");

            Assert.False(File.Exists(Path.Combine(storeExtra, "a.json")));
            Assert.Equal("{\"v\":1}", File.ReadAllText(Path.Combine(storeExtra, "b.json")));
        }
        finally
        {
            Cleanup(scriptId);
        }
    }

    [Fact]
    public void ExtraKeyIsStableAndPathSpecific()
    {
        string a = ConfigSwapPaths.ExtraKey("D:/games/DATA/CONFIGS/software_config.json");
        string b = ConfigSwapPaths.ExtraKey("D:/games/DATA/CONFIGS/software_config.json");
        string c = ConfigSwapPaths.ExtraKey("D:/games/User/config.json");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith("x", a, StringComparison.Ordinal);
    }

    private static void Cleanup(string scriptId)
    {
        try
        {
            string dir = Path.Combine(AppPaths.DataDir, scriptId);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
        }
    }
}
