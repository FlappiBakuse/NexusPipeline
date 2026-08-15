namespace NexusPipeline.Utilities;

/// <summary>
/// 测试时间加速钩子（v0.6.2）：仅当环境变量 NEXUS_TIME_SCALE 设置（1-1000）时生效，生产不设置 = 行为零变化。
/// 缩放宿主内与墙钟相关的等待：监控循环间隔、判断脚本周期触发、成功标志等待退出宽限、日志无更新超时、
/// 运行总时间超时、判断脚本执行超时、游戏启动确认轮询。缩放值注入判断脚本输入 JSON（timeScale 字段），
/// 供测试判断脚本按比例缩放内部墙钟常量（如卡住判定阈值）。
/// </summary>
internal static class TestHooks
{
    /// <summary>时间缩放因子（墙钟秒 / 实际等待秒），默认 1 = 不加速。</summary>
    public static int TimeScale { get; } = ReadScale();

    private static int ReadScale()
    {
        string? raw = Environment.GetEnvironmentVariable("NEXUS_TIME_SCALE");
        if (int.TryParse(raw, out int scale) && scale >= 1 && scale <= 1000)
        {
            return scale;
        }
        return 1;
    }

    /// <summary>缩放墙钟毫秒：ms / TimeScale，下限 10ms（避免零延迟忙轮询）。</summary>
    public static int ScaledMs(int ms)
    {
        return Math.Max(10, ms / TimeScale);
    }

    /// <summary>缩放墙钟秒：sec / TimeScale，下限 1 秒（保留语义可观测性，如周期触发与超时的先后关系）。</summary>
    public static int ScaledSeconds(int seconds)
    {
        return Math.Max(1, seconds / TimeScale);
    }

    /// <summary>测试用 adb 可执行文件路径（v0.7.0+，env NEXUS_ADB_EXE）：e2e 用 stub adb 模拟模拟器命令，生产不设置。</summary>
    public static string? AdbExe => Environment.GetEnvironmentVariable("NEXUS_ADB_EXE");
}
