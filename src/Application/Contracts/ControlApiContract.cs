namespace NexusPipeline.App.Contracts;

/// <summary>Control API 身份契约；CLI 发现服务时必须同时校验服务名与协议版本。</summary>
internal static class ControlApiContract
{
    public const string ServiceName = "NexusPipeline";

    public const int Version = 1;
}
