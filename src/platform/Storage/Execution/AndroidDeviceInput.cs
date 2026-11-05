using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class AndroidDeviceInput(AndroidTargetStage target, StepEvidenceBinding binding,
    Action<Action<OwnedWorkerJob, OwnedWorkerIdentity>> useParent, ExecutionTimeBudget budget, Func<bool> expired)
{
    private PreparedAndroidDevice? _device;
    private long _outputBytes;
    private bool _closed;

    internal async ValueTask<ExecutionDeviceResponse> InvokeAsync(ExecutionDeviceRequest request, CancellationToken cancellation)
    {
        RequireLive(cancellation);
        var response = new ExecutionDeviceResponse { Sequence = request.Sequence, Kind = request.Kind, Success = false };
        if (request.Kind == "inactive") return response with { Success = true };
        var timeout = budget.Limit(TimeSpan.FromMilliseconds(request.TimeoutMilliseconds ?? 60000));
        if (timeout <= TimeSpan.Zero) throw new IOException("execution_device_budget_expired");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        _device ??= target.PrepareDevice(binding);
        await _device.RequireCurrentAsync(deadline.Token).ConfigureAwait(false);
        var frozen = _device.Use(value => value);
        var arguments = Arguments(frozen, request);
        if (arguments is null) return response;
        if (frozen.Driver == "mumu") await RequireMappingAsync(frozen, deadline.Token).ConfigureAwait(false);
        var result = await RunAsync(frozen, arguments, timeout, deadline.Token).ConfigureAwait(false);
        if (frozen.Driver == "mumu") await RequireMappingAsync(frozen, deadline.Token).ConfigureAwait(false);
        RequireLive(cancellation);
        var text = Decode(result.StandardOutput);
        var success = result.ExitCode == 0;
        if (request.Kind == "connected" && frozen.Driver == "genericAdb") success &= text.Trim() == "device";
        return response with { Success = success, Text = request.Kind == "shell" && success ? text : null };
    }

    private async Task RequireMappingAsync(ExecutionTargetAndroid frozen, CancellationToken cancellation)
    {
        var mapping = frozen.Mapping ?? throw new IOException("execution_device_mapping_unbound");
        if (mapping.ManagerImageSha256 != frozen.Tool.Sha256) throw new IOException("execution_device_mapping_unbound");
        var result = await RunAsync(frozen, ["info", "-v", "all"], budget.Limit(TimeSpan.FromSeconds(5)), cancellation).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("execution_device_mapping_unverified");
        MuMuInfoParser.RequireCaptureMapping(Decode(result.StandardOutput), mapping.Index, frozen.Endpoint);
    }

    private async Task<AndroidDeviceOutput> RunAsync(ExecutionTargetAndroid frozen, string[] arguments, TimeSpan timeout, CancellationToken cancellation)
    {
        RequireLive(cancellation);
        if (timeout <= TimeSpan.Zero || _outputBytes >= 67108864) throw new IOException("execution_device_budget_expired");
        var result = await AndroidDeviceCommand.RunAsync(_device!, frozen.Tool, arguments, useParent,
            timeout, target.StopTimeout, expired, cancellation).ConfigureAwait(false);
        _outputBytes = checked(_outputBytes + result.StandardOutput.Length + result.StandardError.Length);
        if (_outputBytes > 67108864) throw new IOException("execution_device_output_limit");
        return result;
    }

    private static string[]? Arguments(ExecutionTargetAndroid target, ExecutionDeviceRequest request)
    {
        if (request.Kind is "connect" or "connected")
            return ExecutionAndroidCommands.DeriveFrozen(target, request.Kind == "connect" ? ExecutionAndroidAction.Connect : ExecutionAndroidAction.DeviceState).Arguments.ToArray();
        if (request.Kind is "startApp" or "stopApp")
        {
            if (request.Intent != target.Package) return null;
            return ExecutionAndroidCommands.DeriveFrozen(target, request.Kind == "startApp" ? ExecutionAndroidAction.StartApplication : ExecutionAndroidAction.StopApplication).Arguments.ToArray();
        }
        var command = request.Kind switch
        {
            "click" => "input tap " + Number(request.X!.Value) + " " + Number(request.Y!.Value),
            "swipe" => "input swipe " + string.Join(" ", new[] { request.X!.Value, request.Y!.Value, request.X2!.Value,
                request.Y2!.Value, request.DurationMilliseconds!.Value }.Select(Number)),
            "key" when request.Phase == 0 => "input keyevent " + Number(request.KeyCode!.Value),
            "text" when request.Text!.All(character => character is >= ' ' and <= '~' && character != '%') =>
                "input text " + Quote(request.Text!.Replace(" ", "%s", StringComparison.Ordinal)),
            "shell" => request.Command!,
            _ => null
        };
        if (command is null) return null;
        return target.Driver switch
        {
            "genericAdb" => ["-s", ExecutionAndroidCommands.Endpoint(target.Endpoint), "shell", command],
            "mumu" when target.Mapping is { } mapping => ["adb", "-v", Number(mapping.Index), "shell", command],
            _ => throw new IOException("execution_device_driver_unavailable")
        };
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Decode(byte[] value)
    {
        try { return new UTF8Encoding(false, true).GetString(value); }
        catch (DecoderFallbackException error) { throw new IOException("execution_device_output_encoding_invalid", error); }
    }
    private void RequireLive(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_closed || expired() || budget.Read().Remaining <= TimeSpan.Zero) throw new IOException("execution_device_input_closed");
    }
    internal void Close() => _closed = true;
}
