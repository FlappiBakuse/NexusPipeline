namespace NexusPipeline.Models;

/// <summary>数据填写约束配置（config/limits.json，启动加载；文件缺失时生成默认值）。字段为 PascalCase。</summary>
internal class AppLimits
{
    public int MaxScripts { get; set; } = 50;

    public int MaxUsersPerScript { get; set; } = 50;

    /// <summary>全局用户实体总量上限。</summary>
    public int MaxUsers { get; set; } = 50;

    public int MaxQueues { get; set; } = 50;

    public int MaxQueueTotalUsers { get; set; } = 50;

    public int MaxTimeSetsPerQueue { get; set; } = 10;

    /// <summary>运行天数配置允许的当前最大值；-1 表示不允许正数运行天数。</summary>
    public int MaxRunDays { get; set; } = 365;

    /// <summary>每日成功运行次数配置允许的当前最大值。</summary>
    public int MaxSuccessfulRunsPerDay { get; set; } = 10;

    public int MinAttempts { get; set; } = 1;

    public int MaxAttempts { get; set; } = 10;

    public int MinStallMinutes { get; set; } = 1;

    public int MaxStallMinutes { get; set; } = 60;

    public int MinTotalMinutes { get; set; } = 5;

    public int MaxTotalMinutes { get; set; } = 720;
}

/// <summary>不进入 limits.json 的固定上限。</summary>
internal static class AppFixedLimits
{
    public const int MaxEntityNameBytes = 64;

    public const int MaxUserRemarkBytes = 512;

    public const int HistoryRetentionDaysMax = 180;
}
