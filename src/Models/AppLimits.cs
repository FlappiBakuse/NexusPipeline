namespace NexusPipeline.Models;

/// <summary>数据填写约束配置（config/limits.json，启动加载，程序只读不写）。字段为 PascalCase。</summary>
internal class AppLimits
{
    public int MaxScripts { get; set; } = 25;

    public int MaxUsersPerScript { get; set; } = 10;

    /// <summary>全局用户实体总量上限（v0.9.6）。</summary>
    public int MaxUsers { get; set; } = 50;

    public int MaxQueues { get; set; } = 10;

    public int MaxTimeSetsPerQueue { get; set; } = 10;

    public int MaxQueueTotalUsers { get; set; } = 50;

    public int MaxScriptNameBytes { get; set; } = 128;

    public int MaxQueueNameBytes { get; set; } = 128;

    public int MinAttempts { get; set; } = 1;

    public int MaxAttempts { get; set; } = 10;

    public int MinStallMinutes { get; set; } = 1;

    public int MaxStallMinutes { get; set; } = 60;

    public int MinTotalMinutes { get; set; } = 5;

    public int MaxTotalMinutes { get; set; } = 720;

    /// <summary>历史保留天数上限（默认 180 天）。</summary>
    public int MaxHistoryRetentionDays { get; set; } = 180;
}
