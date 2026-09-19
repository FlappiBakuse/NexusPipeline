using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Host.Composition.Adapters;

/// <summary>把平台完成退出请求接到宿主生命周期，保持 Platform 不依赖 Host。</summary>
internal sealed class ApplicationExitRequestAdapter : IApplicationExitRequest
{
    public bool TryRequest() => Bootstrap.TryRequestCompletionExit();
}
