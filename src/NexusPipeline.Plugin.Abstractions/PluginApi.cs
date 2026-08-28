using System.Text.Json.Nodes;

namespace NexusPipeline.Plugin.Abstractions;

/// <summary>稳定的 NexusPipeline managed-code 插件生命周期契约（Plugin API v1.3）。</summary>
public static class PluginApiVersion
{
    public const int Major = 1;

    public const int Minor = 3;
}

/// <summary>独立于 C# Plugin API 维护的前端扩展 ABI 版本。</summary>
public static class FrontendApiVersion
{
    public const int Major = 1;

    public const int Minor = 0;

    public const string Text = "1.0";

    public static bool IsCompatibleWith(string? value)
    {
        string[] parts = (value ?? "").Trim().Split('.', StringSplitOptions.None);
        return parts.Length == 2
            && parts.All(part => part.Length > 0 && part.All(ch => ch is >= '0' and <= '9'))
            && parts.All(part => part.Length == 1 || part[0] != '0')
            && int.TryParse(parts[0], out int major)
            && int.TryParse(parts[1], out int minor)
            && major == Major
            && minor <= Minor;
    }
}

/// <summary>v1.3 的稳定 UI 槽位。槽位是前端 ABI 的一部分，页面布局可以变化但槽位名称保持兼容。</summary>
public static class PluginUiSlots
{
    public const string DashboardCards = "dashboard.cards";
    public const string DashboardAfterRunning = "dashboard.after-running";
    public const string UsersListBadges = "users.list.badges";
    public const string UsersBindingSections = "users.binding.sections";
    public const string UsersGlobalSections = "users.global.sections";
    public const string ScriptsListBadges = "scripts.list.badges";
    public const string ScriptsEditorSections = "scripts.editor.sections";
    public const string QueuesListBadges = "queues.list.badges";
    public const string QueuesEditorSections = "queues.editor.sections";
    public const string DispatchCards = "dispatch.cards";
    public const string DispatchRunningBadges = "dispatch.running.badges";
    public const string DispatchRunSections = "dispatch.run.sections";
    public const string HistoryListBadges = "history.list.badges";
    public const string HistoryDetailSections = "history.detail.sections";
    public const string SettingsSections = "settings.sections";
    public const string ShellNav = "shell.nav";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        DashboardCards,
        DashboardAfterRunning,
        UsersListBadges,
        UsersBindingSections,
        UsersGlobalSections,
        ScriptsListBadges,
        ScriptsEditorSections,
        QueuesListBadges,
        QueuesEditorSections,
        DispatchCards,
        DispatchRunningBadges,
        DispatchRunSections,
        HistoryListBadges,
        HistoryDetailSections,
        SettingsSections,
        ShellNav,
    };
}

public static class PluginUiContributionKinds
{
    public const string Form = "form";
    public const string Badge = "badge";
    public const string Card = "card";
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

/// <summary>Plugin API v1.2 的用户列表展示扩展；v1.1 插件仍可继续使用 IPluginHostContextV1_1。</summary>
public interface IPluginHostContextV1_2 : IPluginHostContextV1_1
{
    IPluginUserListBadgeRegistry UserListBadges { get; }
}

/// <summary>Plugin API v1.3 的通用 UI、作用域数据、插件 Web API 与历史展示端口。</summary>
public interface IPluginHostContextV1_3 : IPluginHostContextV1_2
{
    IPluginUiContributionRegistry Ui { get; }

    IPluginScopedDataStore ScopedData { get; }

    IPluginWebApiRegistry WebApi { get; }

    IPluginHistoryContributionRegistry History { get; }
}

/// <summary>声明式 UI 贡献注册表。贡献处理器只接收稳定的 PluginUiContext 与 JSON DTO。</summary>
public interface IPluginUiContributionRegistry
{
    IDisposable Register(PluginUiContribution contribution);
}

public sealed record PluginUiContext(
    string Slot,
    string Mode = "",
    string PrimaryId = "",
    string SecondaryId = "");

public sealed record PluginUiOption(string Value, string Label);

public sealed record PluginUiField(
    string Key,
    string Label,
    string Type,
    string Description = "",
    bool Required = false,
    string Placeholder = "",
    int MaxLength = 0,
    IReadOnlyList<PluginUiOption>? Options = null,
    bool ReadOnly = false,
    double? Min = null,
    double? Max = null,
    double? Step = null);

public sealed record PluginUiContribution(
    string Id,
    string Slot,
    string Kind,
    string Title,
    string Description = "",
    int Order = 0,
    IReadOnlyList<PluginUiField>? Fields = null,
    Func<PluginUiContext, CancellationToken, ValueTask<JsonObject?>>? ReadHandler = null,
    Func<PluginUiContext, JsonObject, CancellationToken, ValueTask>? SaveHandler = null,
    Func<PluginUiContext, string, JsonObject, CancellationToken, ValueTask<JsonObject?>>? ActionHandler = null)
{
    public static PluginUiContribution Form(
        string id,
        string slot,
        string title,
        IReadOnlyList<PluginUiField> fields,
        Func<PluginUiContext, CancellationToken, ValueTask<JsonObject?>> readHandler,
        Func<PluginUiContext, JsonObject, CancellationToken, ValueTask>? saveHandler = null,
        string description = "",
        int order = 0,
        Func<PluginUiContext, string, JsonObject, CancellationToken, ValueTask<JsonObject?>>? actionHandler = null) =>
        new(id, slot, PluginUiContributionKinds.Form, title, description, order, fields, readHandler, saveHandler, actionHandler);

    public static PluginUiContribution Badge(
        string id,
        string slot,
        Func<PluginUiContext, CancellationToken, ValueTask<JsonObject?>> readHandler,
        int order = 0,
        string title = "") =>
        new(id, slot, PluginUiContributionKinds.Badge, title, Order: order, ReadHandler: readHandler);

    public static PluginUiContribution Card(
        string id,
        string slot,
        string title,
        Func<PluginUiContext, CancellationToken, ValueTask<JsonObject?>> readHandler,
        string description = "",
        int order = 0,
        Func<PluginUiContext, string, JsonObject, CancellationToken, ValueTask<JsonObject?>>? actionHandler = null) =>
        new(id, slot, PluginUiContributionKinds.Card, title, description, order, ReadHandler: readHandler, ActionHandler: actionHandler);
}

/// <summary>按逻辑实体作用域隔离的插件 JSON 存储；scope 不得包含绝对路径或越界段。</summary>
public interface IPluginScopedDataStore
{
    ValueTask<T?> ReadAsync<T>(string scope, CancellationToken cancellationToken = default);

    ValueTask WriteAsync<T>(string scope, T value, CancellationToken cancellationToken = default);

    ValueTask<JsonObject?> ReadJsonAsync(string scope, CancellationToken cancellationToken = default);

    ValueTask WriteJsonAsync(string scope, JsonObject value, CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(string scope, CancellationToken cancellationToken = default);
}

/// <summary>插件自有 Web API 注册表。路由最终位于 /api/plugin-api/{pluginName}/ 下。</summary>
public interface IPluginWebApiRegistry
{
    IDisposable Register(PluginWebApiRoute route);
}

public sealed record PluginWebApiRoute(
    string Method,
    string Route,
    Func<PluginWebApiRequest, CancellationToken, ValueTask<PluginWebApiResponse>> Handler);

public sealed record PluginWebApiRequest(
    string Method,
    string Route,
    IReadOnlyDictionary<string, string> Query,
    string? JsonBody);

public sealed record PluginWebApiResponse(int StatusCode, JsonNode? JsonBody)
{
    public static PluginWebApiResponse Json(JsonNode? body, int statusCode = 200) => new(statusCode, body);

    public static PluginWebApiResponse Empty(int statusCode = 204) => new(statusCode, null);
}

/// <summary>运行历史落盘前的插件展示贡献。该端口不能改变运行结果或阻断执行。</summary>
public interface IPluginHistoryContributionRegistry
{
    IDisposable Register(PluginHistoryContribution contribution);
}

public sealed record PluginHistoryContribution(
    string Id,
    int Order,
    Func<PluginHistoryContext, CancellationToken, ValueTask<PluginHistoryDisplay?>> Handler);

public sealed record PluginHistoryContext(
    string RunId,
    string UserId,
    string UserName,
    string ScriptInstanceId,
    string ScriptName,
    string QueueId,
    string QueueName,
    string Mode,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Status);

public sealed record PluginHistoryDisplay(
    string Id,
    string Title,
    IReadOnlyList<PluginUiBadge>? Badges = null,
    IReadOnlyList<PluginUiFieldValue>? Fields = null);

public sealed record PluginUiBadge(string Label, string Tone = "muted", string Title = "");

public sealed record PluginUiFieldValue(string Label, string Value, string Tone = "muted");

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

/// <summary>插件声明式用户列表徽章贡献；处理器只接收用户 ID，不应执行网络请求。</summary>
public interface IPluginUserListBadgeRegistry
{
    IDisposable Register(PluginUserListBadgeContribution contribution);
}

public sealed record PluginUserListBadgeContribution(
    string Id,
    int Order,
    Func<string, CancellationToken, ValueTask<PluginUserListBadge?>> ReadHandler);

public sealed record PluginUserListBadge(
    string Label,
    string Tone = "muted",
    string Title = "");

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
