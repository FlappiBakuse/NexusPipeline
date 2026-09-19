using System.Collections.Concurrent;
using System.Text;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
namespace NexusPipeline.Modules.Configuration.Editing;


/// <summary>
/// 配置储存管理对外门面（拆分）：保持全部外部 API 签名不变。
/// 实现分层：文件原语 <see cref="ConfigSwapPrimitives"/>（安全移动/原子替换/重试/跨进程互斥）、
/// 会话与恢复 <see cref="ConfigSwapSession"/>（.session 标记/门禁/回滚/finally 还原/启动扫描恢复）、
/// 数据目录 <see cref="ConfigPaths"/>。数据保全序：original（原配置）&gt; config &gt; store（可重建）。
/// </summary>
internal static class ConfigEditSessionRegistry
{
    public static readonly ConcurrentDictionary<string, EditSession> EditSessions = new();
}
