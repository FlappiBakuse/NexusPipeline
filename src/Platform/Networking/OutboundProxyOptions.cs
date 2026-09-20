namespace NexusPipeline.Platform.Networking;

/// <summary>一次外部 HTTP 请求使用的、已经从宿主设置投影出的代理参数。</summary>
internal sealed record OutboundProxyOptions(
    string Mode,
    string Url,
    string Username,
    string Password)
{
    public static OutboundProxyOptions Direct { get; } = new("none", "", "", "");
}
