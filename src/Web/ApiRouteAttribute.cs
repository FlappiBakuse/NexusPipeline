namespace NexusPipeline.Web;

internal enum ApiBodyMode
{
    JsonText,
    Raw,
}

/// <summary>
/// API 资源路由特性：标注在 handler 类（资源名，如 [ApiRoute("scripts")]）或方法（子路由，如 cancel）上，
/// WebServer 启动时反射扫描注册到路由表，新增 API 无需再改 WebServer 路由 switch。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
internal sealed class ApiRouteAttribute : Attribute
{
    public ApiRouteAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public ApiBodyMode BodyMode { get; init; } = ApiBodyMode.JsonText;

    public int MaxBodyBytes { get; init; } = 10 * 1024 * 1024;
}
