using NexusPipeline.ControlPlane.Http.Services;
using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Host.Composition.Adapters;

/// <summary>将 Platform 原生路径选择器接入 ControlPlane 端口。</summary>
internal sealed class NativePathPickerAdapter : INativePathPicker
{
    private readonly NativePathPickerService _picker;

    public NativePathPickerAdapter(NativePathPickerService picker)
    {
        _picker = picker;
    }

    public Task<string?> PickAsync(NativePathPickerRequest request) => _picker.PickAsync(request);

    public bool IsExistingDirectory(string? path) => NativePathPickerService.IsExistingDirectory(path);
}
