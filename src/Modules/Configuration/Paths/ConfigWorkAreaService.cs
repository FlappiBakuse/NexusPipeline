using System.Collections.Concurrent;
using System.Text;
using NexusPipeline.Modules.Configuration.Exchange;
namespace NexusPipeline.Modules.Configuration.Paths;


/// <summary>
/// 配置储存管理对外门面（拆分）：保持全部外部 API 签名不变。
/// 实现分层：文件原语 <see cref="ConfigSwapPrimitives"/>（安全移动/原子替换/重试/跨进程互斥）、
/// 会话与恢复 <see cref="ConfigSwapSession"/>（.session 标记/门禁/回滚/finally 还原/启动扫描恢复）、
/// 数据目录 <see cref="ConfigPaths"/>。数据保全序：original（原配置）&gt; config &gt; store（可重建）。
/// </summary>
internal static class ConfigWorkAreaService
{

    /// <summary>准备判断脚本目录：清空重建（运行开始调用）。</summary>
    public static void PrepareScriptDir(string scriptId, string? userName)
    {
        ConfigPaths.PrepareScriptDir(scriptId, userName);
    }

    /// <summary>运行结束清理：清空判断脚本目录与配置替换备份目录。</summary>
    public static void CleanupScriptArea(string scriptId, string? userName)
    {
        ConfigPaths.CleanupScriptArea(scriptId, userName);
    }

    /// <summary>删除脚本时清理其全部数据目录。</summary>
    public static bool RemoveScriptData(string scriptId)
    {
        return ConfigPaths.RemoveScriptData(scriptId);
    }

    /// <summary>删除用户绑定时清理其 UserId 数据目录。</summary>
    public static void RemoveUserData(string scriptId, string userKey)
    {
        ConfigPaths.RemoveUserData(scriptId, userKey);
    }
}
