namespace NexusPipeline.Plugin.Abstractions;

/// <summary>稳定的 NexusPipeline managed-code 插件生命周期契约（Plugin API v1）。</summary>
public interface INexusPlugin
{
    ValueTask InitializeAsync(IPluginHostContext context, CancellationToken cancellationToken);

    ValueTask StartAsync(CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>插件可消费的宿主服务集合；不暴露宿主 DI 容器或业务领域模型。</summary>
public interface IPluginHostContext
{
    string PluginName { get; }

    IPluginLogger Logger { get; }

    IPluginConfigStore Config { get; }

    IPluginSecretStore Secrets { get; }

    IPluginNotificationService Notifications { get; }

    IPluginJobScheduler Scheduler { get; }
}

/// <summary>带插件上下文的最小日志端口。</summary>
public interface IPluginLogger
{
    void Debug(string message);

    void Info(string message);

    void Warn(string message);

    void Error(string message);
}

/// <summary>插件专属 JSON 配置存储。宿主决定落盘位置和序列化格式。</summary>
public interface IPluginConfigStore
{
    ValueTask<T?> ReadAsync<T>(CancellationToken cancellationToken = default);

    ValueTask WriteAsync<T>(T value, CancellationToken cancellationToken = default);
}

/// <summary>插件专属 DPAPI 密钥存储。value 为空表示清除密钥。</summary>
public interface IPluginSecretStore
{
    ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    ValueTask SetAsync(string key, string? value, CancellationToken cancellationToken = default);
}

/// <summary>插件通知消费者端口。通知系统由宿主拥有，插件只提交宿主无关 DTO。</summary>
public interface IPluginNotificationService
{
    ValueTask SendAsync(PluginNotification notification, CancellationToken cancellationToken = default);
}

public sealed record PluginNotification(string Title, string Body);

/// <summary>插件后台任务调度端口。任务由宿主隔离执行并在插件停止时取消。</summary>
public interface IPluginJobScheduler
{
    IDisposable Register(
        PluginJobDefinition definition,
        Func<PluginJobContext, CancellationToken, ValueTask> handler);
}

/// <summary>后台任务定义。Interval 与 DailyTime 至少设置一个；两者同时设置时按二者任一到期触发。</summary>
public sealed record PluginJobDefinition(
    string Id,
    TimeSpan? Interval = null,
    TimeOnly? DailyTime = null,
    TimeSpan? Timeout = null);

public sealed record PluginJobContext(
    string PluginName,
    string JobId,
    DateTimeOffset ScheduledAt,
    DateTimeOffset StartedAt);
