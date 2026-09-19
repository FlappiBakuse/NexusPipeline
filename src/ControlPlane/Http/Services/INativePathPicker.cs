using NexusPipeline.Platform.Windows;

namespace NexusPipeline.ControlPlane.Http.Services;

/// <summary>HTTP 控制面使用的原生路径选择端口；实现由 Host 组合 Platform 服务。</summary>
internal interface INativePathPicker
{
    Task<string?> PickAsync(NativePathPickerRequest request);

    bool IsExistingDirectory(string? path);
}
