using System.Collections.Concurrent;
using System.Text;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Users;
namespace NexusPipeline.Modules.Configuration.Recovery;


/// <summary>
/// 配置储存管理对外门面（拆分）：保持全部外部 API 签名不变。
/// 实现分层：文件原语 <see cref="ConfigSwapPrimitives"/>（安全移动/原子替换/重试/跨进程互斥）、
/// 会话与恢复 <see cref="ConfigSwapSession"/>（.session 标记/门禁/回滚/finally 还原/启动扫描恢复）、
/// 数据目录 <see cref="ConfigPaths"/>。数据保全序：original（原配置）&gt; config &gt; store（可重建）。
/// </summary>
internal static class ConfigRecoveryService
{

    /* ---------------- 恢复（转发 ConfigSwapSession；运行期替换/同步/重试由 ConfigRunSession 直达 ConfigSwapSession） ---------------- */

    /// <summary>启动恢复：按当前全局用户绑定的 UserId 白名单处理会话标记与配置替换，并保留脚本级现场。</summary>
    public static void RecoverInterrupted(IReadOnlyList<NexusUser>? users = null)
    {
        ConfigSwapSession.RecoverInterrupted(users);
    }

    /// <summary>启动后台恢复重试循环：每 10 秒尝试还原待办项（孤儿进程退出/文件解锁后自动完成），直至全部成功或进程退出。</summary>
    public static void StartRecoveryRetry()
    {
        ConfigSwapSession.StartRecoveryRetry();
    }

    public static void StopRecoveryRetry()
    {
        ConfigSwapSession.StopRecoveryRetry();
    }
}
