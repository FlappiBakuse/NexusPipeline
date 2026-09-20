using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Host.Composition.Adapters;

/// <summary>把平台完成退出请求接到宿主生命周期，保持 Platform 不依赖 Host。</summary>
internal sealed class ApplicationExitRequestAdapter : IApplicationExitRequest
{
    private readonly Func<bool> _request;

    public ApplicationExitRequestAdapter(Func<bool> request)
    {
        _request = request;
    }

    public bool TryRequest() => _request();
}
