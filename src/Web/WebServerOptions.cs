namespace NexusPipeline.Web;

/// <summary>常驻 HTTP 控制面的监听选项。轻量模式只关闭静态 Web UI，不关闭本机 API。</summary>
internal sealed record WebServerOptions(bool ServeWebUi, bool AllowRemoteAccess)
{
    public static WebServerOptions FromSettings(bool lightweight, bool allowRemoteAccess)
    {
        return new WebServerOptions(
            ServeWebUi: !lightweight,
            AllowRemoteAccess: !lightweight && allowRemoteAccess);
    }
}
