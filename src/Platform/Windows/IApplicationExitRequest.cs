namespace NexusPipeline.Platform.Windows;

/// <summary>宿主提供的退出请求端口；平台系统操作不依赖宿主生命周期实现。</summary>
internal interface IApplicationExitRequest
{
    bool TryRequest();
}
