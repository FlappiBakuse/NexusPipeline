using NexusPipeline.Extensibility;
using NexusPipeline.Plugins;

namespace NexusPipeline.Services.Execution;

internal sealed record ExecutionPreviewResponse(
    int StatusCode,
    byte[]? Data = null,
    string ContentType = "image/jpeg",
    string? State = null,
    string? Source = null,
    DateTimeOffset? CapturedAt = null,
    string? Error = null);

/// <summary>运行中游戏画面的受控读取服务。只解析宿主保存的当前运行目标，不接受客户端进程/窗口/ADB 参数。</summary>
internal sealed class ExecutionPreviewService
{
    private readonly Func<DispatchCenter> _center;
    private readonly Func<PluginManager> _plugins;

    public ExecutionPreviewService(Func<DispatchCenter> center, Func<PluginManager> plugins)
    {
        _center = center;
        _plugins = plugins;
    }

    public async Task<ExecutionPreviewResponse> CaptureAsync(
        string runId,
        string pluginName,
        CancellationToken cancellationToken = default)
    {
        if (!_plugins().IsKnownPlugin(pluginName)
            || !_plugins().HasCapability(pluginName, PluginCapabilityKeys.ExecutionPreviewClient)
            || !_plugins().IsEnabled(pluginName)
            || !_plugins().HasFrontend(pluginName))
        {
            return new ExecutionPreviewResponse(404, Error: "执行预览插件不可用");
        }

        RunningExecution? execution = _center().Active.FirstOrDefault(item =>
            string.Equals(item.Id, runId, StringComparison.OrdinalIgnoreCase));
        if (execution is null)
        {
            return new ExecutionPreviewResponse(404, Error: "未找到正在运行的任务");
        }
        if (!execution.TryBeginPreviewCapture())
        {
            return Waiting("waiting_for_game", "");
        }

        try
        {
            ExecutionPreviewTarget? target = execution.PreviewTarget;
            if (target is null || target.Source == ExecutionPreviewSource.None)
            {
                return Waiting("waiting_for_game", "");
            }
            if (target.State == ExecutionPreviewState.Unavailable)
            {
                return Waiting("waiting_for_game", SourceText(target.Source));
            }
            if (target.Source == ExecutionPreviewSource.Pc)
            {
                if (target.ProcessId is not int processId || processId <= 0)
                {
                    return Waiting("waiting_for_game", "pc");
                }
                ExecutionPreviewImageResult image = await Task.Run(
                    () => ExecutionPreviewImage.CapturePc(processId),
                    cancellationToken).ConfigureAwait(false);
                return image.Ok
                    ? Ready(image.Data, "pc")
                    : Waiting("window_not_ready", "pc");
            }
            if (target.EmulatorDriver is null)
            {
                return Waiting("emulator_not_ready", "emulator");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            EmulatorBinaryResult binary = await target.EmulatorDriver
                .CaptureScreenAsync(timeout.Token, 8)
                .ConfigureAwait(false);
            if (!binary.Ok)
            {
                return Waiting("emulator_not_ready", "emulator");
            }
            ExecutionPreviewImageResult converted = ExecutionPreviewImage.ConvertPng(binary.Data);
            return converted.Ok
                ? Ready(converted.Data, "emulator")
                : Waiting("emulator_not_ready", "emulator");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Waiting("emulator_not_ready", "emulator");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ExecutionPreviewResponse(500, Error: SanitizeError(ex));
        }
        finally
        {
            execution.EndPreviewCapture();
        }
    }

    private static ExecutionPreviewResponse Ready(byte[] data, string source) =>
        new(200, data, "image/jpeg", Source: source, CapturedAt: DateTimeOffset.Now);

    private static ExecutionPreviewResponse Waiting(string state, string source) =>
        new(204, State: state, Source: source);

    private static string SourceText(ExecutionPreviewSource source) => source switch
    {
        ExecutionPreviewSource.Pc => "pc",
        ExecutionPreviewSource.Emulator => "emulator",
        _ => "",
    };

    private static string SanitizeError(Exception exception)
    {
        string message = exception.Message.Trim();
        return message.Length <= 240 ? message : message[..240] + "…";
    }
}
