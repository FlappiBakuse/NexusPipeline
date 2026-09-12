using System.Text.Json.Nodes;

namespace NexusPipeline.Plugin.Abstractions;

/// <summary>稳定的 NexusPipeline managed-code 插件生命周期契约（Plugin API v1.6）。</summary>
public static class PluginApiVersion
{
    public const int Major = 1;

    public const int Minor = 6;
}

/// <summary>独立于 C# Plugin API 维护的前端扩展 ABI 版本；要求精确版本匹配。</summary>
public static class FrontendApiVersion
{
    public const int Major = 1;

    public const int Minor = 5;

    public const string Text = "1.5";

    public static bool IsCompatibleWith(string? value)
    {
        return string.Equals(value, Text, StringComparison.Ordinal);
    }
}

/// <summary>v1.4 的稳定 UI 槽位。槽位是前端 ABI 的一部分，页面布局可以变化但槽位名称保持兼容。</summary>
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
    public const string DispatchRunningSidecar = "dispatch.running.sidecar";
    public const string DispatchRunSections = "dispatch.run.sections";
    public const string HistoryListBadges = "history.list.badges";
    public const string HistoryDetailSections = "history.detail.sections";
    public const string SettingsSections = "settings.sections";
    public const string SettingsCards = "settings.cards";
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
        DispatchRunningSidecar,
        DispatchRunSections,
        HistoryListBadges,
        HistoryDetailSections,
        SettingsSections,
        SettingsCards,
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

/// <summary>Plugin API v1.5 的插件自有本地化能力。语言由宿主请求作用域决定。</summary>
public interface IPluginHostContextV1_4 : IPluginHostContextV1_3
{
    IPluginLocalization I18n { get; }
}

/// <summary>Plugin API v1.6 的通用二进制资产端口；资产按插件命名空间隔离，宿主不解释资产业务含义。</summary>
public interface IPluginHostContextV1_6 : IPluginHostContextV1_4
{
    IPluginAssetStore Assets { get; }
}

/// <summary>插件二进制资产元数据。Id 为内容 SHA256 的小写十六进制，相同内容重复写入返回同一 Id。</summary>
public sealed record PluginAssetInfo(
    string Id,
    string Scope,
    string Extension,
    long SizeBytes,
    DateTimeOffset CreatedAt);

/// <summary>插件二进制资产读取句柄；Content 由调用方负责释放。</summary>
public sealed class PluginAssetContent : IDisposable
{
    public PluginAssetContent(PluginAssetInfo info, Stream content)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        Content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public PluginAssetInfo Info { get; }

    public Stream Content { get; }

    public void Dispose() => Content.Dispose();
}

/// <summary>
/// 按插件命名空间与逻辑 scope 隔离的二进制资产存储。宿主要负责路径逃逸防护、原子写入和宿主级绝对上限，
/// 业务配额、去重策略与资产含义由插件自行决定。
/// </summary>
public interface IPluginAssetStore
{
    /// <summary>写入资产；返回的 Id 是内容 SHA256。scope 与 extension 由宿主校验。</summary>
    ValueTask<PluginAssetInfo> WriteAsync(
        string scope,
        string extension,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>按 Id 打开资产；不存在时返回 null。</summary>
    ValueTask<PluginAssetContent?> OpenAsync(
        string scope,
        string assetId,
        CancellationToken cancellationToken = default);

    /// <summary>删除资产；返回是否存在并已删除。</summary>
    ValueTask<bool> DeleteAsync(
        string scope,
        string assetId,
        CancellationToken cancellationToken = default);

    /// <summary>枚举 scope 内的资产元数据，按创建时间与 Id 稳定排序。</summary>
    ValueTask<IReadOnlyList<PluginAssetInfo>> ListAsync(
        string scope,
        CancellationToken cancellationToken = default);
}

/// <summary>插件文案的语义引用；Fallback 用于语言资源缺失时的安全回退。</summary>
public sealed record PluginLocalizedText(string Key, string Fallback = "");

/// <summary>带命名插值参数的插件动态文案；适合状态、结果和历史字段值。</summary>
public sealed record PluginLocalizedValue(
    string Key,
    string Fallback = "",
    IReadOnlyDictionary<string, object?>? Args = null);

/// <summary>插件可预期地展示给用户的校验或业务异常；宿主负责按请求语言解析。</summary>
public sealed class PluginUserVisibleException : Exception
{
    public PluginUserVisibleException(
        string code,
        string messageKey,
        string fallback,
        IReadOnlyDictionary<string, object?>? args = null)
        : base(fallback)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("插件用户异常 code 不能为空", nameof(code));
        if (string.IsNullOrWhiteSpace(messageKey)) throw new ArgumentException("插件用户异常 messageKey 不能为空", nameof(messageKey));
        Code = code.Trim();
        MessageKey = messageKey.Trim();
        Fallback = fallback ?? "";
        Args = args is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(args, StringComparer.Ordinal);
    }

    public string Code { get; }

    public string MessageKey { get; }

    public string Fallback { get; }

    public IReadOnlyDictionary<string, object?> Args { get; }
}

public interface IPluginLocalization
{
    string Locale { get; }

    string DefaultLocale { get; }

    string T(string key, string fallback = "", IReadOnlyDictionary<string, object?>? args = null);

    string FormatNumber(double value);

    string FormatDate(DateTimeOffset value);

    string FormatTime(DateTimeOffset value);
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

public sealed record PluginUiOption(string Value, string Label)
{
    public PluginLocalizedText? LocalizedLabel { get; init; }
};

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
    double? Step = null)
{
    public PluginLocalizedText? LocalizedLabel { get; init; }

    public PluginLocalizedText? LocalizedDescription { get; init; }

    public PluginLocalizedText? LocalizedPlaceholder { get; init; }
};

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
    public PluginLocalizedText? LocalizedTitle { get; init; }

    public PluginLocalizedText? LocalizedDescription { get; init; }

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
    string? JsonBody)
{
    /// <summary>请求 Content-Type（小写、去掉参数）；没有请求体时为空字符串。</summary>
    public string ContentType { get; init; } = "";

    /// <summary>请求体字节数；长度未知时为 -1。</summary>
    public long ContentLength { get; init; } = -1;

    /// <summary>打开原始请求体流；没有请求体时为 null。流由宿主在本次调用期间持有，调用方不需要释放。</summary>
    public Func<CancellationToken, ValueTask<Stream>>? OpenBodyStream { get; init; }
}

public sealed record PluginWebApiResponse(int StatusCode, JsonNode? JsonBody)
{
    /// <summary>二进制响应体；由调用方创建，宿主读取后负责释放。</summary>
    public Stream? BinaryBody { get; init; }

    /// <summary>二进制响应的 Content-Type；必须属于 <see cref="PluginWebApiContentTypes.AllowedBinary"/>。</summary>
    public string ContentType { get; init; } = "";

    /// <summary>二进制响应字节数；未知时为 -1，由宿主按流长度决定。</summary>
    public long ContentLength { get; init; } = -1;

    public static PluginWebApiResponse Json(JsonNode? body, int statusCode = 200) => new(statusCode, body);

    public static PluginWebApiResponse Empty(int statusCode = 204) => new(statusCode, null);

    /// <summary>构造二进制响应。contentType 不在宿主允许集合内时，宿主按插件错误处理。</summary>
    public static PluginWebApiResponse Binary(
        Stream content,
        string contentType,
        int statusCode = 200,
        long contentLength = -1) =>
        new(statusCode, null)
        {
            BinaryBody = content ?? throw new ArgumentNullException(nameof(content)),
            ContentType = contentType ?? "",
            ContentLength = contentLength,
        };
}

/// <summary>插件 Web API 二进制响应的类型约束；宿主只回传图片与通用二进制流。</summary>
public static class PluginWebApiContentTypes
{
    /// <summary>宿主允许插件回传的二进制 Content-Type（同源环境下不提供可执行文档类型）。</summary>
    public static IReadOnlySet<string> AllowedBinary { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/webp",
        "image/gif",
        "image/avif",
        "application/octet-stream",
    };

    /// <summary>去掉参数并转为小写；无效输入返回空字符串。</summary>
    public static string Normalize(string? contentType)
    {
        string value = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        return value.Length > 128 ? "" : value;
    }

    public static bool IsAllowedBinary(string? contentType) => AllowedBinary.Contains(Normalize(contentType));
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
    IReadOnlyList<PluginUiFieldValue>? Fields = null)
{
    public PluginLocalizedText? LocalizedTitle { get; init; }
};

public sealed record PluginUiBadge(string Label, string Tone = "muted", string Title = "")
{
    public PluginLocalizedText? LocalizedLabel { get; init; }

    public PluginLocalizedText? LocalizedTitle { get; init; }
};

public sealed record PluginUiFieldValue(string Label, string Value, string Tone = "muted")
{
    public PluginLocalizedText? LocalizedLabel { get; init; }

    public PluginLocalizedValue? LocalizedValue { get; init; }
};

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
    Func<string, System.Text.Json.Nodes.JsonObject, CancellationToken, ValueTask> SaveHandler)
{
    public PluginLocalizedText? LocalizedTitle { get; init; }

    public PluginLocalizedText? LocalizedDescription { get; init; }
};

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
    bool ReadOnly = false)
{
    public PluginLocalizedText? LocalizedLabel { get; init; }

    public PluginLocalizedText? LocalizedDescription { get; init; }

    public PluginLocalizedText? LocalizedPlaceholder { get; init; }
};

public sealed record PluginUserGlobalManagementOption(string Value, string Label)
{
    public PluginLocalizedText? LocalizedLabel { get; init; }
};

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
    string Title = "")
{
    public PluginLocalizedText? LocalizedLabel { get; init; }

    public PluginLocalizedText? LocalizedTitle { get; init; }
};

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
