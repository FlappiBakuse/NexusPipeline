using System.Collections.Concurrent;
using System.Text;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
namespace NexusPipeline.Modules.Configuration.Snapshots;


/// <summary>
/// 配置储存管理对外门面（拆分）：保持全部外部 API 签名不变。
/// 实现分层：文件原语 <see cref="ConfigSwapPrimitives"/>（安全移动/原子替换/重试/跨进程互斥）、
/// 会话与恢复 <see cref="ConfigSwapSession"/>（.session 标记/门禁/回滚/finally 还原/启动扫描恢复）、
/// 数据目录 <see cref="ConfigPaths"/>。数据保全序：original（原配置）&gt; config &gt; store（可重建）。
/// </summary>
internal static class ConfigSnapshotService
{

    /* ---------------- 对外操作 ---------------- */

    /// <summary>判断用户在脚本实例上是否已有配置快照（store 目录存在且非空）；首次编辑配置以此为准。</summary>
    public static bool HasSnapshot(string scriptId, string userName)
    {
        string store = ConfigPaths.StoreDir(scriptId, userName);
        return Directory.Exists(store) && Directory.EnumerateFileSystemEntries(store).Any();
    }
}
