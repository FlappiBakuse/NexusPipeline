namespace NexusPipeline.Services;

/// <summary>
/// 当前宿主进程的实例标识。
/// 实例标识在本进程内保持不变，重启后新进程生成新值，控制面前端据此确认应答来自新实例；
/// 重启交接标识由拉起子进程的旧进程生成，并随命令行传给子进程，用于确认应答来自本次重启的新进程。
/// </summary>
internal static class HostInstance
{
    public static string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>本次进程由重启交接拉起时携带的交接标识；正常启动为空。</summary>
    public static string RestartHandoffId { get; private set; } = "";

    /// <summary>归一化启动参数中的交接标识：只接受 1 至 64 位不含控制字符的标识，其余按普通启动处理。</summary>
    internal static string NormalizeRestartHandoff(string? handoffId)
    {
        string value = (handoffId ?? "").Trim();
        if (value.Length is 0 or > 64)
        {
            return "";
        }
        return value.Any(char.IsControl) ? "" : value;
    }

    /// <summary>记录启动参数中的重启交接标识；非重启启动保持为空。</summary>
    public static void AdoptRestartHandoff(string? handoffId) => RestartHandoffId = NormalizeRestartHandoff(handoffId);
}
