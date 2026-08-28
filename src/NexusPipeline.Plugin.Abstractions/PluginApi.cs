namespace NexusPipeline.Plugin.Abstractions;

/// <summary>稳定的 NexusPipeline managed-code 插件生命周期契约（Plugin API v1.1）。</summary>
public static class PluginApiVersion
{
    public const int Major = 1;

    public const int Minor = 1;
}

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

/// <summary>Plugin API v1.1 的附加宿主能力；扩展接口保持 v1.0 插件二进制兼容。</summary>
public interface IPluginHostContextV1_1 : IPluginHostContext
{
    IPluginUserDataStore UserData { get; }

    IPluginUserGlobalManagementRegistry UserGlobalManagement { get; }

    IPluginExecutionEventService ExecutionEvents { get; }

    IPluginHttpClientFactory Http { get; }
}

/// <summary>按用户隔离的插件配置与 DPAPI 密钥存储。</summary>
public interface IPluginUserDataStore
{
    ValueTask<T?> ReadConfigAsync<T>(string userId, CancellationToken cancellationToken = default);

    ValueTask WriteConfigAsync<T>(string userId, T value, CancellationToken cancellationToken = default);

    ValueTask<string?> GetSecretAsync(string userId, string key, CancellationToken cancellationToken = default);

    ValueTask SetSecretAsync(string userId, string key, string? value, CancellationToken cancellationToken = default);
}

/// <summary>插件声明式用户全局设置贡献注册表。</summary>
public interface IPluginUserGlobalManagementRegistry
{
    IDisposable Register(PluginUserGlobalManagementContribution contribution);
}

/// <summary>宿主可渲染的用户全局设置贡献；处理器只接收用户 ID 和 JSON 值。</summary>
public sealed record PluginUserGlobalManagementContribution(
    string Id,
    string Title,
    string Description,
    int Order,
    IReadOnlyList<PluginUserGlobalManagementField> Fields,
    Func<string, CancellationToken, ValueTask<System.Text.Json.Nodes.JsonObject>> ReadHandler,
    Func<string, System.Text.Json.Nodes.JsonObject, CancellationToken, ValueTask> SaveHandler);

/// <summary>用户全局设置字段的有限声明式类型集合。</summary>
public sealed record PluginUserGlobalManagementField(
    string Key,
    string Label,
    string Type,
    string Description = "",
    bool Required = false,
    string Placeholder = "",
    int MaxLength = 0,
    IReadOnlyList<PluginUserGlobalManagementOption>? Options = null,
    bool ReadOnly = false);

public sealed record PluginUserGlobalManagementOption(string Value, string Label);

/// <summary>用户脚本执行开始事件；仅暴露稳定的宿主无关标识与时间信息。</summary>
public interface IPluginExecutionEventService
{
    IDisposable SubscribeUserRunStarting(Func<PluginUserRunStartingEvent, ValueTask> handler);
}

public sealed record PluginUserRunStartingEvent(
    string UserId,
    string UserName,
    string ScriptInstanceId,
    string ScriptName,
    string QueueId,
    string QueueName,
    string Mode,
    DateTimeOffset StartedAt);

/// <summary>插件外网请求出口；代理和宿主设置由宿主内部决定。</summary>
public interface IPluginHttpClientFactory
{
    HttpClient CreateClient(
        Uri? destination = null,
        TimeSpan? timeout = null,
        bool allowAutoRedirect = false);
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
